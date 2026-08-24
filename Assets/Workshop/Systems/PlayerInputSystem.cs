using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Workshop
{
    /// <summary>
    /// The ONLY managed system in this project, and deliberately NOT [BurstCompile].
    ///
    /// InputAction is a managed class, so it cannot cross into Burst-compiled code.
    /// This system is the boundary: managed API in, blittable data out. Everything
    /// downstream of the PlayerInput component is Burst all the way.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class PlayerInputSystem : SystemBase
    {
        private InputAction _move;
        private InputAction _aim;

        protected override void OnCreate()
        {
            // Built once in OnCreate and cached. Reading an action per frame allocates
            // nothing; subscribing to its callbacks would capture and allocate.
            _move = new InputAction("Move", InputActionType.Value);
            _move.AddCompositeBinding("2DVector")
                 .With("Up", "<Keyboard>/w")
                 .With("Down", "<Keyboard>/s")
                 .With("Left", "<Keyboard>/a")
                 .With("Right", "<Keyboard>/d");
            _move.AddBinding("<Gamepad>/leftStick");

            _aim = new InputAction("Aim", InputActionType.Value);
            _aim.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/upArrow")
                .With("Down", "<Keyboard>/downArrow")
                .With("Left", "<Keyboard>/leftArrow")
                .With("Right", "<Keyboard>/rightArrow");
            _aim.AddBinding("<Gamepad>/rightStick");

            RequireForUpdate<PlayerInput>();
        }

        protected override void OnStartRunning()
        {
            _move.Enable();
            _aim.Enable();
        }

        protected override void OnStopRunning()
        {
            _move.Disable();
            _aim.Disable();
        }

        protected override void OnDestroy()
        {
            _move?.Dispose();
            _aim?.Dispose();
            _move = null;
            _aim = null;
        }

        protected override void OnUpdate()
        {
            var move = (float2)_move.ReadValue<Vector2>();
            var aim = (float2)_aim.ReadValue<Vector2>();

            // Aim falls back to the movement direction so keyboard-only players still fire.
            if (math.lengthsq(aim) < 0.01f)
            {
                aim = move;
            }

            SystemAPI.SetSingleton(new PlayerInput { Move = move, Aim = aim });
        }
    }
}
