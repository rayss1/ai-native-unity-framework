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
        private CharacterControls _controls;
        private InputActionMap _gameplay;
        private InputActionMap _ui;
        private CharacterInputFrame _pending;
        private bool _focused = true;
        private bool _captured;
        private InputActionReference[] _uiReferences;
        public bool IsCaptured => _captured;
        public InputActionAsset Actions => _controls?.asset;

        public void ConfigureUi(InputSystemUIInputModule module) => uiModule = module;

        private void Awake()
        {
            _controls = new CharacterControls();
            _gameplay = _controls.asset.FindActionMap("Gameplay", true);
            _ui = _controls.asset.FindActionMap("UI", true);
            if (uiModule != null)
            {
                uiModule.actionsAsset = _controls.asset;
                _uiReferences = new InputActionReference[6];
                string[] names = { "Point", "Click", "Scroll", "Navigate", "Submit", "Cancel" };
                for (int i = 0; i < names.Length; i++)
                {
                    _uiReferences[i] = InputActionReference.Create(_ui.FindAction(names[i], true));
                }

                uiModule.point = _uiReferences[0];
                uiModule.leftClick = _uiReferences[1];
                uiModule.scrollWheel = _uiReferences[2];
                uiModule.move = _uiReferences[3];
                uiModule.submit = _uiReferences[4];
                uiModule.cancel = _uiReferences[5];
            }
        }

        private void OnEnable()
        {
            _gameplay.actionTriggered += OnAction;
            _ui.FindAction("Click", true).performed += OnUiClick;
            _ui.Enable();
            SetCaptured(true);
        }

        private void OnDisable()
        {
            SetCaptured(false);
            _gameplay.actionTriggered -= OnAction;
            _ui.FindAction("Click", true).performed -= OnUiClick;
            _ui.Disable();
        }

        private void OnDestroy()
        {
            if (_uiReferences != null)
            {
                foreach (var reference in _uiReferences)
                {
                    Destroy(reference);
                }
            }

            _controls?.Dispose();
        }

        private bool _recaptureRequested;

        private void OnUiClick(InputAction.CallbackContext context)
        {
            if (!_captured && _focused && context.ReadValue<float>() > .5f)
            {
                _recaptureRequested = true;
            }
        }

        private void Update()
        {
            // UI 在 Update 处理指针状态；重新捕获前必须等待按键释放，
            // 防止关闭 UI 的点击被当作攻击。
            if (_recaptureRequested && !_ui.FindAction("Click").IsPressed())
            {
                _recaptureRequested = false;
                if (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject())
                {
                    SetCaptured(true);
                }
            }
        }

        private void OnApplicationFocus(bool value)
        {
            _focused = value;
            if (!value)
            {
                SetCaptured(false);
            }
        }

        public void SetCaptured(bool value)
        {
            _captured = value && _focused && isActiveAndEnabled;
            Clear();
            _recaptureRequested = false;
            if (_captured)
            {
                _gameplay.Enable();
            }
            else
            {
                _gameplay.Disable();
            }

            Cursor.lockState = _captured ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !_captured;
        }

        public void Clear() => _pending = default;

        private void OnAction(InputAction.CallbackContext context)
        {
            if (!_captured || !context.performed)
            {
                return;
            }

            switch (context.action.name)
            {
                case "Jump":
                    _pending.Jump = true;
                    break;
                case "Crouch":
                    _pending.Crouch = true;
                    break;
                case "Roll":
                    _pending.Roll = true;
                    break;
                case "Attack":
                    _pending.Attack = true;
                    break;
                case "FreeMode":
                    _pending.ChangeMode = true;
                    _pending.Mode = CharacterMode.Free;
                    break;
                case "ShooterMode":
                    _pending.ChangeMode = true;
                    _pending.Mode = CharacterMode.Shooter;
                    break;
                case "MeleeMode":
                    _pending.ChangeMode = true;
                    _pending.Mode = CharacterMode.Melee;
                    break;
                case "ReleaseCursor":
                    SetCaptured(false);
                    break;
            }
        }

        public CharacterInputFrame ConsumeFrame()
        {
            if (!_captured)
            {
                Clear();
                return default;
            }

            var frame = _pending;
            Clear();
            frame.Move = Vector2.ClampMagnitude(
                _gameplay.FindAction("Move").ReadValue<Vector2>(),
                1f
            );
            frame.Look = _gameplay.FindAction("Look").ReadValue<Vector2>();
            frame.Sprint = _gameplay.FindAction("Sprint").IsPressed();
            frame.Walk = _gameplay.FindAction("Walk").IsPressed();
            frame.Aim = _gameplay.FindAction("Aim").IsPressed();
            return frame;
        }
    }
}
