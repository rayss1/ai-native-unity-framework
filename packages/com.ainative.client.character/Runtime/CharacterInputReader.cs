using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace AiNative.Client.Character
{
    [DefaultExecutionOrder(-100), DisallowMultipleComponent]
    public sealed class CharacterInputReader : MonoBehaviour
    {
        [SerializeField]
        private InputSystemUIInputModule uiModule;
        private CharacterControls controls;
        private InputActionMap gameplay,
            ui;
        private CharacterInputFrame pending;
        private bool focused = true,
            captured;
        private InputActionReference[] uiReferences;
        public bool IsCaptured => captured;
        public InputActionAsset Actions => controls?.asset;

        public void ConfigureUi(InputSystemUIInputModule module) => uiModule = module;

        private void Awake()
        {
            controls = new CharacterControls();
            gameplay = controls.asset.FindActionMap("Gameplay", true);
            ui = controls.asset.FindActionMap("UI", true);
            if (uiModule != null)
            {
                uiModule.actionsAsset = controls.asset;
                uiReferences = new InputActionReference[6];
                string[] names = { "Point", "Click", "Scroll", "Navigate", "Submit", "Cancel" };
                for (int i = 0; i < names.Length; i++)
                    uiReferences[i] = InputActionReference.Create(ui.FindAction(names[i], true));
                uiModule.point = uiReferences[0];
                uiModule.leftClick = uiReferences[1];
                uiModule.scrollWheel = uiReferences[2];
                uiModule.move = uiReferences[3];
                uiModule.submit = uiReferences[4];
                uiModule.cancel = uiReferences[5];
            }
        }

        private void OnEnable()
        {
            gameplay.actionTriggered += OnAction;
            ui.FindAction("Click", true).performed += OnUiClick;
            ui.Enable();
            SetCaptured(true);
        }

        private void OnDisable()
        {
            SetCaptured(false);
            gameplay.actionTriggered -= OnAction;
            ui.FindAction("Click", true).performed -= OnUiClick;
            ui.Disable();
        }

        private void OnDestroy()
        {
            if (uiReferences != null)
                foreach (var reference in uiReferences)
                    Destroy(reference);
            controls?.Dispose();
        }

        private bool recaptureRequested;

        private void OnUiClick(InputAction.CallbackContext context)
        {
            if (!captured && focused && context.ReadValue<float>() > .5f)
                recaptureRequested = true;
        }

        private void Update()
        {
            // UI processes pointer state during Update. A release is required before recapturing,
            // so the click that closes UI can never be consumed as an attack.
            if (recaptureRequested && !ui.FindAction("Click").IsPressed())
            {
                recaptureRequested = false;
                if (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject())
                    SetCaptured(true);
            }
        }

        private void OnApplicationFocus(bool value)
        {
            focused = value;
            if (!value)
                SetCaptured(false);
        }

        public void SetCaptured(bool value)
        {
            captured = value && focused && isActiveAndEnabled;
            Clear();
            recaptureRequested = false;
            if (captured)
                gameplay.Enable();
            else
                gameplay.Disable();
            Cursor.lockState = captured ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !captured;
        }

        public void Clear() => pending = default;

        private void OnAction(InputAction.CallbackContext context)
        {
            if (!captured || !context.performed)
                return;
            switch (context.action.name)
            {
                case "Jump":
                    pending.Jump = true;
                    break;
                case "Crouch":
                    pending.Crouch = true;
                    break;
                case "Roll":
                    pending.Roll = true;
                    break;
                case "Attack":
                    pending.Attack = true;
                    break;
                case "FreeMode":
                    pending.ChangeMode = true;
                    pending.Mode = CharacterMode.Free;
                    break;
                case "ShooterMode":
                    pending.ChangeMode = true;
                    pending.Mode = CharacterMode.Shooter;
                    break;
                case "MeleeMode":
                    pending.ChangeMode = true;
                    pending.Mode = CharacterMode.Melee;
                    break;
                case "ReleaseCursor":
                    SetCaptured(false);
                    break;
            }
        }

        public CharacterInputFrame ConsumeFrame()
        {
            if (!captured)
            {
                Clear();
                return default;
            }
            var frame = pending;
            Clear();
            frame.Move = Vector2.ClampMagnitude(
                gameplay.FindAction("Move").ReadValue<Vector2>(),
                1f
            );
            frame.Look = gameplay.FindAction("Look").ReadValue<Vector2>();
            frame.Sprint = gameplay.FindAction("Sprint").IsPressed();
            frame.Walk = gameplay.FindAction("Walk").IsPressed();
            frame.Aim = gameplay.FindAction("Aim").IsPressed();
            return frame;
        }
    }
}
