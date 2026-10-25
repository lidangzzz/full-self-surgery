using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace SofaRobotAI.Agent
{
    /// <summary>
    /// LLM-backed high-level planner. At each decision point it sends a compact
    /// observation of the arm cell to an OpenAI-compatible /chat/completions
    /// endpoint and expects a JSON action back:
    ///   {"skill":"MoveTo|Grasp|Release|Home|Idle","target":[x,y,z],"reason":"..."}
    /// While a request is in flight Decide() returns null (the agent keeps
    /// executing its current skill); after a timeout or on any error the brain
    /// falls back to <see cref="HeuristicBrain"/> so the cell never stalls.
    /// Configure the endpoint on <see cref="MultiArmAgentManager"/>; the API key
    /// is read from the SOFAROBOT_LLM_API_KEY environment variable if unset.
    /// </summary>
    public class LlmAgentBrain : IAgentBrain
    {
        public string Name => "LLM";

        private readonly HeuristicBrain _fallback = new HeuristicBrain();
        private readonly MonoBehaviour _host; // coroutine runner
        private readonly string _endpoint;
        private readonly string _model;
        private readonly string _apiKey;
        private readonly float _timeoutSeconds;

        private AgentObservation _pending;
        private AgentDecision _result;
        private bool _requestInFlight;
        private float _flightStart;
        private int _calls, _failures;

        public LlmAgentBrain(MonoBehaviour host, string endpoint, string model, string apiKey, float timeoutSeconds = 20f)
        {
            _host = host;
            _endpoint = endpoint;
            _model = model;
            _apiKey = string.IsNullOrEmpty(apiKey)
                ? Environment.GetEnvironmentVariable("SOFAROBOT_LLM_API_KEY") : apiKey;
            _timeoutSeconds = timeoutSeconds;
        }

        public void Initialize(Core.RobotArmController arm) { }

        public AgentDecision Decide(AgentObservation obs)
        {
            // Timed out or endpoint dead -> keep the cell working via the heuristic policy.
            if (_requestInFlight && Time.time - _flightStart > _timeoutSeconds)
            {
                _failures++;
                _requestInFlight = false;
                return _fallback.Decide(obs);
            }
            if (_requestInFlight) return null;      // still thinking
            if (_result != null)                    // previous request finished
            {
                var decision = _result;
                _result = null;
                return decision;
            }
            if (string.IsNullOrEmpty(_endpoint) || string.IsNullOrEmpty(_apiKey))
                return _fallback.Decide(obs);       // not configured

            _pending = obs;
            _result = null;
            _requestInFlight = true;
            _flightStart = Time.time;
            _host.StartCoroutine(RequestDecision(obs));
            return null;
        }

        private IEnumerator RequestDecision(AgentObservation obs)
        {
            _calls++;
            string prompt = BuildPrompt(obs);
            var body = new ChatRequest
            {
                model = _model,
                temperature = 0.2f,
                messages = new[]
                {
                    new ChatMessage { role = "system", content = SystemPrompt },
                    new ChatMessage { role = "user", content = prompt },
                }
            };

            using var req = new UnityWebRequest(_endpoint, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body))),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = Mathf.CeilToInt(_timeoutSeconds),
            };
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + _apiKey);

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                _failures++;
                Debug.LogWarning($"[LlmAgentBrain] request failed: {req.error} (falling back)");
            }
            else
            {
                var resp = JsonUtility.FromJson<ChatResponse>(req.downloadHandler.text);
                string content = resp?.choices != null && resp.choices.Length > 0
                    ? resp.choices[0].message.content : null;
                _result = ParseAction(content);
                if (_result == null)
                {
                    _failures++;
                    Debug.LogWarning($"[LlmAgentBrain] unparseable reply: {content}");
                }
            }
            _requestInFlight = false;
        }

        private const string SystemPrompt =
            "You are the motion planner of a robotic arm in a pick-and-place cell. " +
            "Answer with ONE json object only, no markdown: " +
            "{\"skill\":\"MoveTo|Grasp|Release|Home|Idle\",\"target\":[x,y,z],\"reason\":\"short\"}. " +
            "Coordinates are Unity world meters (Y up, floor at 0). Grasp only when close (<0.6m) to the payload. " +
            "Release only when holding and near the drop position.";

        private static string BuildPrompt(AgentObservation o) =>
            $"Arm {o.armName} ({o.dof} DOF) at TCP ({Fmt(o.tcpPosition)}), speed {o.tcpSpeed:F2} m/s, " +
            $"holding={o.holdingPayload} ({o.heldObjectName ?? "none"}). " +
            $"Task: {o.taskDescription}. Pick at ({Fmt(o.pickPosition)}), dist {o.distanceToPick:F2} m. " +
            $"Drop at ({Fmt(o.dropPosition)}), dist {o.distanceToDrop:F2} m. " +
            $"ArrivedAtTarget={o.arrivedAtTarget}. Payloads left in cell: {o.payloadsRemaining}. t={o.simulationTime:F0}s. " +
            "Decide the next action.";

        private static string Fmt(Vector3 v) => $"{v.x:F2},{v.y:F2},{v.z:F2}";

        private static AgentDecision ParseAction(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            int start = content.IndexOf('{');
            int end = content.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            string json = content.Substring(start, end - start + 1);

            try
            {
                var action = JsonUtility.FromJson<LlmAction>(json);
                if (string.IsNullOrEmpty(action.skill)) return null;
                action.skill = action.skill.Trim('\"');
                if (!Enum.TryParse(action.skill, true, out AgentSkill skill)) return null;
                var d = new AgentDecision { skill = skill, reason = action.reason ?? "" };
                if (skill == AgentSkill.MoveTo && action.target != null && action.target.Length == 3)
                    d.target = new Vector3(action.target[0], action.target[1], action.target[2]);
                return d;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LlmAgentBrain] parse error: {e.Message}");
                return null;
            }
        }

        public string Stats => $"llm calls={_calls} failures={_failures}";

        // --- OpenAI-compatible wire types --------------------------------- //
        [Serializable] private class ChatMessage { public string role; public string content; }
        [Serializable] private class ChatRequest
        {
            public string model;
            public float temperature;
            public ChatMessage[] messages;
        }
        [Serializable] private class ChatChoice { public ChatMessage message; }
        [Serializable] private class ChatResponse { public ChatChoice[] choices; }
        [Serializable] private class LlmAction
        {
            public string skill;
            public float[] target;
            public string reason;
        }
    }
}
