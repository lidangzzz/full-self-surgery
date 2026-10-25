using UnityEngine;

namespace SofaRobotAI.Core
{
    /// <summary>
    /// A graspable cube the agents manipulate. Tracks which arm holds it and
    /// highlights its task state (waiting / carried / delivered).
    /// </summary>
    [RequireComponent(typeof(Rigidbody), typeof(BoxCollider))]
    public class PayloadObject : MonoBehaviour
    {
        public Rigidbody Rigidbody { get; private set; }
        public RobotArmController HeldBy { get; private set; }
        public bool IsGrasped => HeldBy != null;
        public bool Delivered { get; set; }

        private Material _mat;
        private Color _baseColor;

        private void Awake()
        {
            Rigidbody = GetComponent<Rigidbody>();
            _mat = GetComponent<Renderer>().material;
            _baseColor = _mat.color;
        }

        public void NotifyGrasped(RobotArmController arm)
        {
            HeldBy = arm;
            _mat.color = Color.Lerp(_baseColor, Color.white, 0.45f);
        }

        public void NotifyReleased()
        {
            HeldBy = null;
            _mat.color = Delivered ? Color.green : _baseColor;
        }

        public void MarkDelivered()
        {
            Delivered = true;
            if (!IsGrasped) _mat.color = Color.green;
        }
    }
}
