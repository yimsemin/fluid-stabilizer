using UnityEngine;
using UnityEngine.InputSystem;

namespace FluidStabilizer.Sim
{
    /// <summary>
    /// Keyboard shortcuts for interactive play plus a simple chase/side/top
    /// camera. Driving input itself is handled by <see cref="HumanDriverInput"/>.
    /// </summary>
    public sealed class PlayControls : MonoBehaviour
    {
        public SimulationRunner Runner;
        public SimView View;
        public Camera Camera;

        private enum CamMode { Chase, Side, Top }
        private CamMode _cam = CamMode.Chase;
        private StabilizerChoice _lastActive = StabilizerChoice.SpecificForceAlignment;
        private bool _slowMo;

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null) HandleKeys(kb);
            UpdateCamera();
        }

        private void HandleKeys(Keyboard kb)
        {
            if (kb.digit0Key.wasPressedThisFrame) Runner.SetStabilizer(StabilizerChoice.Off);
            if (kb.digit1Key.wasPressedThisFrame) SetActive(StabilizerChoice.SpecificForceAlignment);
            if (kb.digit2Key.wasPressedThisFrame) SetActive(StabilizerChoice.SpecificForceAlignmentPlusI);
            if (kb.digit3Key.wasPressedThisFrame) SetActive(StabilizerChoice.PrivilegedFeedforward);

            if (kb.spaceKey.wasPressedThisFrame) // toggle stabilizer on/off
            {
                if (Runner.ActiveStabilizer == StabilizerChoice.Off)
                    Runner.SetStabilizer(_lastActive);
                else
                {
                    _lastActive = Runner.ActiveStabilizer;
                    Runner.SetStabilizer(StabilizerChoice.Off);
                }
            }

            if (kb.rKey.wasPressedThisFrame) Runner.ResetEpisode();

            if (kb.mKey.wasPressedThisFrame) // cycle the slosh model (pendulum ROM / shallow water)
                Runner.SetSloshModel(Runner.ActiveSloshModel == SloshModelChoice.PendulumRom
                    ? SloshModelChoice.ShallowWater
                    : SloshModelChoice.PendulumRom);

            if (kb.tKey.wasPressedThisFrame)
            {
                _slowMo = !_slowMo;
                Runner.TimeScale = _slowMo ? 0.25f : 1f;
            }

            if (kb.tabKey.wasPressedThisFrame)
                _cam = (CamMode)(((int)_cam + 1) % 3);
        }

        private void SetActive(StabilizerChoice c)
        {
            _lastActive = c;
            Runner.SetStabilizer(c);
        }

        private void UpdateCamera()
        {
            if (Camera == null || View?.TruckBody == null) return;
            Transform t = View.TruckBody;

            Vector3 targetPos;
            Quaternion targetRot;
            switch (_cam)
            {
                case CamMode.Side:
                    targetPos = t.position + t.right * -6f + Vector3.up * 3.2f;
                    targetRot = Quaternion.LookRotation(t.position + Vector3.up * 2.4f - targetPos);
                    break;
                case CamMode.Top:
                    targetPos = t.position + Vector3.up * 14f + t.forward * 2.6f;
                    targetRot = Quaternion.LookRotation(Vector3.down, t.forward);
                    break;
                default: // Chase
                    targetPos = t.position + t.forward * -8f + Vector3.up * 4.8f;
                    targetRot = Quaternion.LookRotation(t.position + Vector3.up * 2.4f - targetPos);
                    break;
            }

            float k = 1f - Mathf.Exp(-8f * Time.deltaTime);
            Camera.transform.position = Vector3.Lerp(Camera.transform.position, targetPos, k);
            Camera.transform.rotation = Quaternion.Slerp(Camera.transform.rotation, targetRot, k);
        }
    }
}
