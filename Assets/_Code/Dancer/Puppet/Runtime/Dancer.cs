using Klak.Math;
using Noise = Klak.Math.NoiseHelper;
using Unity.Mathematics;
using UnityEngine;

namespace Puppet
{
    public class Dancer : MonoBehaviour
    {
        #region Editable fields

        [SerializeField] float _footDistance  = 0.3f;
        [SerializeField] float _stepFrequency = 2;
        [SerializeField] float _stepHeight    = 0.3f;
        [SerializeField] float _stepAngle     = 90;
        [SerializeField] float _maxDistance   = 2;

        [SerializeField] float _hipHeight        = 0.9f;
        [SerializeField] float _hipPositionNoise = 0.1f;
        [SerializeField] float _hipRotationNoise = 30;

        [SerializeField] float   _spineBend          = -8;
        [SerializeField] Vector3 _spineRotationNoise = new Vector3(30, 20, 20);

        [SerializeField] Vector3 _handPosition      = new Vector3(0.3f, 0.3f, -0.2f);
        [SerializeField] Vector3 _handPositionNoise = new Vector3(0.3f, 0.3f, 0.3f);

        [SerializeField] float _headMove = 3;

        [SerializeField] float _noiseFrequency = 1.1f;
        [SerializeField] uint  _randomSeed     = 123;

        #endregion

        #region Public accessor properties

        public float footDistance {
            get { return _footDistance; }
            set { _footDistance = value; }
        }

        public float stepFrequency {
            get { return _stepFrequency; }
            set { _stepFrequency = value; }
        }

        public float stepHeight {
            get { return _stepHeight; }
            set { _stepHeight = value; }
        }

        public float stepAngle {
            get { return _stepAngle; }
            set { _stepAngle = value; }
        }

        public float maxDistance {
            get { return _maxDistance; }
            set { _maxDistance = value; }
        }

        //

        public float hipHeight {
            get { return _hipHeight; }
            set { _hipHeight = value; }
        }

        public float hipPositionNoise {
            get { return _hipPositionNoise; }
            set { _hipPositionNoise = value; }
        }

        public float hipRotationNoise {
            get { return _hipRotationNoise; }
            set { _hipRotationNoise = value; }
        }

        //

        public float spineBend {
            get { return _spineBend; }
            set { _spineBend = value; }
        }

        public Vector3 spineRotationNoise {
            get { return _spineRotationNoise; }
            set { _spineRotationNoise = value; }
        }

        //

        public Vector3 handPosition {
            get { return _handPosition; }
            set { _handPosition = value; }
        }

        public Vector3 handPositionNoise {
            get { return _handPositionNoise; }
            set { _handPositionNoise = value; }
        }

        //

        public float headMove {
            get { return _headMove; }
            set { _headMove = value; }
        }

        //

        public float noiseFrequency {
            get { return _noiseFrequency; }
            set { _noiseFrequency = value; }
        }

        public uint randomSeed {
            get { return _randomSeed; }
            set { _randomSeed = value; }
        }

        #endregion

        #region Private variables

        Animator _animator;

        XXHash _hash;
        float2 _noise;

        // Foot positions
        Vector3[] _feet = new Vector3[2];

        // Timer for step animation
        float _step;

        // Sign of the step angle (turn direction)
        float _stepSign = 1;

        // Transformation matrices of the chest bone. Used to calculate the
        // hand positions. These matrices are unavailable in OnAnimatorIK, so
        // cache them in Update.
        Matrix4x4 _chestMatrix, _chestMatrixInv;

        bool _flipHands = false;

        #endregion

        #region Local math functions

        static Vector3 SetY(Vector3 v, float y)
        {
            v.y = y;
            return v;
        }

        static float SmoothStep(float x)
        {
            return x * x * (3 - 2 * x);
        }

        #endregion

        #region Local properties and functions for foot animation

        // What number the current step is
        int StepCount { get { return Mathf.FloorToInt(_step); } }

        // Progress of the current step
        float StepTime { get { return _step - Mathf.Floor(_step); } }

        // Random seed for the current step
        uint StepSeed { get { return (uint)StepCount * 100; } }

        // Is the pivot in the current step the left foot?
        bool PivotIsLeft { get { return (StepCount & 1) == 0; } }

        // Angle of the pivot rotation in the current step.
        float StepAngle {
            get {
                return _hash.Float(0.5f, 1.0f, StepSeed + 1) * _stepAngle * _stepSign;
            }
        }

        // Pivot rotation at the end of the current step.
        Quaternion StepRotationFull {
            get {
                return Quaternion.AngleAxis(StepAngle, Vector3.up);
            }
        }

        // Pivot rotation at the current time.
        Quaternion StepRotation {
            get {
                return Quaternion.AngleAxis(StepAngle * StepTime, Vector3.up);
            }
        }

        // The original height of the foot point.
        Vector3 FootBias {
            get {
                return Vector3.up * _animator.leftFeetBottomHeight;
            }
        }

        // Calculates the foot position
        Vector3 GetFootPosition(int index)
        {
            var thisFoot = _feet[index];
            var thatFoot = _feet[(index + 1) & 1];

            // If it's the pivot foot, return it immediately.
            if (PivotIsLeft ^ (index == 1))
                return thisFoot + FootBias;

            // Calculate the relative position from the pivot foot.
            var rp = StepRotation * (thisFoot - thatFoot);

            // Vertical move: Sine wave with smooth step
            var up = Mathf.Sin(SmoothStep(StepTime) * Mathf.PI) * _stepHeight;

            return thatFoot + rp + up * Vector3.up + FootBias;
        }

        Vector3 LeftFootPosition { get { return GetFootPosition(0); } }
        Vector3 RightFootPosition { get { return GetFootPosition(1); } }

        // Destination foot point of the current step
        Vector3 CurrentStepDestination {
            get {
                var right = (_feet[1] - _feet[0]).normalized * _footDistance;
                if (PivotIsLeft)
                    return _feet[0] + StepRotationFull * right;
                else
                    return _feet[1] - StepRotationFull * right;
            }
        }

        #endregion

        #region Local properties and functions for body animation

        // Body (hip) position
        Vector3 BodyPosition {
            get {
                // Move so that it keeps the center of mass on the pivot fott.
                var theta = (StepTime + (PivotIsLeft ? 0 : 1)) * Mathf.PI;
                var right = (1 - Mathf.Sin(theta)) / 2;
                var pos   = Vector3.Lerp(LeftFootPosition, RightFootPosition, right);

                // Vertical move: Two wave while one step. Add noise.
                var y  = _hipHeight + Mathf.Cos(StepTime * Mathf.PI * 4) * _stepHeight / 2;
                y     += noise.snoise(_noise) * _hipPositionNoise;

                return SetY(pos, y);
            }
        }

        // Body (hip) rotation
        Quaternion BodyRotation {
            get {
                // Base rotation
                var rot = Quaternion.AngleAxis(-90, Vector3.up);

                // Right vector from the foot positions
                var right = SetY(RightFootPosition - LeftFootPosition, 0);

                // Horizontal rotation from the right vector.
                rot *= Quaternion.LookRotation(right.normalized);

                // Add noise.
                return rot * Noise.Rotation(_noise, math.radians(_hipRotationNoise), 1);
            }
        }

        #endregion

        #region Local properties and functions for upper body animation

        // Spine (spine/chest/upper chest) rotation
        Quaternion SpineRotation {
            get {
                var rot  = Quaternion.AngleAxis(_spineBend * (_flipHands ? -1f : 1f), Vector3.forward);
                rot     *= Noise.Rotation(_noise, math.radians(_spineRotationNoise), 2);
                return rot;
            }
        }

        // Calculates the hand position
        Vector3 GetHandPosition(int index)
        {
            var isLeft = (index == 0) ^ _flipHands;

            // Relative position of the hand.
            var pos = _handPosition;
            if (isLeft)
                pos.x *= -1;

            // Apply the body (hip) transform.
            pos = _animator.bodyRotation * pos + _animator.bodyPosition;

            // Add noise.
            pos += Vector3.Scale(Noise.Float3(_noise, (uint)(4 + index)), _handPositionNoise);

            // Clamping in the local space of the chest bone.
            pos   = _chestMatrixInv * new Vector4(pos.x, pos.y, pos.z, 1);
            pos.y = Mathf.Max(pos.y, 0.2f);
            pos.z = isLeft ? Mathf.Max(pos.z, 0.2f) : Mathf.Min(pos.z, -0.2f);
            pos   = _chestMatrix * new Vector4(pos.x, pos.y, pos.z, 1);

            return pos;
        }

        Vector3 LeftHandPosition { get { return GetHandPosition(0); } }
        Vector3 RightHandPosition { get { return GetHandPosition(1); } }

        // Look at position (for head movement)
        Vector3 LookAtPosition {
            get {
                var pos = Noise.Float3(_noise, 3) * _headMove;
                pos.z   = 2;
                return _animator.bodyPosition + _animator.bodyRotation * pos;
            }
        }

        #endregion

        #region MonoBehaviour implementation

        void OnValidate()
        {
            _footDistance = Mathf.Max(_footDistance, 0.01f);
        }

        void Start()
        {
            _animator = GetComponent<Animator>();

            // Random number/noise generators
            _hash  = new XXHash(_randomSeed);
            _noise = _hash.Float2(-1000, 1000, 0);

            // Initial foot positions
            var origin = SetY(transform.position, 0);
            var foot   = transform.right * _footDistance / 2;
            _feet[0]   = origin - foot;
            _feet[1]   = origin + foot;

            if (RecursiveFindChild(transform, "Neo_Neck") != null)
                _flipHands = true;
        }

        void Update()
        {
            // Noise update
            _noise.x += _noiseFrequency * Time.deltaTime;

            // Step time delta
            var delta = _stepFrequency * Time.deltaTime;

            // Right vector (a vector indicating from left foot to right foot)
            // Renormalized every frame to apply _footDistance changes.
            var right = (_feet[1] - _feet[0]).normalized * _footDistance;

            // Check if the current step ends in this frame.
            if (StepCount == Mathf.FloorToInt(_step + delta))
            {
                // The current step keeps going:
                //  Recalculate the non-pivot foot position.
                //  (To apply _footDistance changes)
                if (PivotIsLeft)
                    _feet[1] = _feet[0] + right;
                else
                    _feet[0] = _feet[1] - right;

                // Update the step time
                _step += delta;
            }
            else
            {
                // The current step is going to end:
                //  Update the next pivot point;
                _feet[PivotIsLeft ? 1 : 0] = CurrentStepDestination;

                // Update the step time
                _step += delta;

                // Flip the turn direction randomly.
                if (_hash.Float(StepSeed) > 0.5f)
                    _stepSign *= -1;

                // Check if the next destination is in the range.
                var dist = (CurrentStepDestination - transform.position).magnitude;
                if (dist > _maxDistance)
                {
                    // The next destination is out of the range:
                    //  Try flipping the turn direction. Use it if it makes
                    //  the destination closer to the origin. Revert it if not.
                    _stepSign *= -1;
                    if (dist < (CurrentStepDestination - transform.position).magnitude)
                        _stepSign *= -1;
                }
            }

            // Update the chest matrices for use in OnAnimatorIK.
            var chest = _animator.GetBoneTransform(HumanBodyBones.Chest);
            //if (chest == null)
            //    chest       = RecursiveFindChild(transform, "Neo_Spine1");
            _chestMatrix    = chest.localToWorldMatrix;
            _chestMatrixInv = chest.worldToLocalMatrix;
        }

        Transform RecursiveFindChild(Transform parent, string childName)
        {
            foreach (Transform child in parent)
            {
                if (child.name == childName)
                {
                    return child;
                }
                else
                {
                    Transform found = RecursiveFindChild(child, childName);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }
            return null;
        }

        void OnAnimatorIK(int layerIndex)
        {
            if (_animator == null)
                return;
            _animator.SetIKPosition(AvatarIKGoal.LeftFoot,  LeftFootPosition);
            _animator.SetIKPosition(AvatarIKGoal.RightFoot, RightFootPosition);
            _animator.SetIKPositionWeight(AvatarIKGoal.LeftFoot,  1);
            _animator.SetIKPositionWeight(AvatarIKGoal.RightFoot, 1);

            _animator.bodyPosition = BodyPosition;
            _animator.bodyRotation = BodyRotation;

            var spine = SpineRotation;
            _animator.SetBoneLocalRotation(HumanBodyBones.Spine,      spine);
            _animator.SetBoneLocalRotation(HumanBodyBones.Chest,      spine);
            _animator.SetBoneLocalRotation(HumanBodyBones.UpperChest, spine);

            _animator.SetIKPosition(AvatarIKGoal.LeftHand,  LeftHandPosition);
            _animator.SetIKPosition(AvatarIKGoal.RightHand, RightHandPosition);
            _animator.SetIKPositionWeight(AvatarIKGoal.LeftHand,  1);
            _animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 1);

            _animator.SetLookAtPosition(LookAtPosition);
            _animator.SetLookAtWeight(1);
        }

        #endregion
    }
}

