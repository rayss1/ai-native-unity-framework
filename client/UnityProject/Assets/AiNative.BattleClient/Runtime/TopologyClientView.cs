using System;
using System.Collections.Generic;
using AiNative.Client.Core.Mvvm;
using AiNative.Client.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace AiNative.Client.Application
{
    /// <summary>仅负责应用 UI；网络所有权仍属于 TopologyClientFlow。</summary>
    [DefaultExecutionOrder(-100)]
    public sealed class TopologyClientView : MonoBehaviour
    {
        [Header("Pages: login, lobby, queue, battle, result")]
        public GameObject[] Pages;
        public Text Status;
        public Text Error;
        public Text Player;
        public Text Statistics;
        public Text PartyHint;
        public Text QueueTime;
        public Text QueueMembers;
        public Text BattleVitals;
        public Text BattleClock;
        public Text BattleConnection;
        public Text Diagnostics;
        public Text ResultTitle;
        public Text ResultStats;
        public InputField Username;
        public InputField Password;
        public InputField Host;
        public InputField Port;
        public InputField Pin;
        public InputField Invite;
        public Toggle Tls;
        public GameObject Advanced;
        public GameObject DiagnosticPanel;
        public Button Login;
        public Button Register;
        public Button AdvancedButton;
        public Button CopyId;
        public Button Create;
        public Button SendInvite;
        public Button Ready;
        public Button Match;
        public Button Leave;
        public Button Cancel;
        public Button Reconnect;
        public Button DiagnosticButton;
        public Button Back;
        public Transform MemberContent;
        public Transform InvitationContent;
        public Text MemberTemplate;
        public Button InvitationTemplate;
        public Image HealthBar;
        public Image ArmorBar;
        public Text BattleKills;
        public Text ResultPlayed;
        public Text ResultWon;
        public Text ResultKills;
        public RectTransform[] ContentColumns;
        public float[] ContentWidths;
        private TopologyClientFlow _flow;
        private TopologyViewModel _viewModel;
        private BindingScope _bindings;
        private KeyedViewList<string, TopologyViewModel.MemberRow, Text> _members;
        private KeyedViewList<string, TopologyViewModel.InvitationRow, InvitationView> _invitations;
        private GameObject _ownedEventSystem;
        private EventSystem _eventSystem;
        private Font _font;
        private int _page = -1;
        private bool _closed;

        private sealed class InvitationView : IDisposable
        {
            public Button Button;
            public AsyncCommand Command;
            public BindingScope Bindings;

            public void Dispose()
            {
                Bindings.Dispose();
                Command.Dispose();
                if (Button != null)
                {
                    Destroy(Button.gameObject);
                }
            }
        }

        private Selectable[] _loginNavigation;
        private Graphic[] _pointerSurfaces;
        private Canvas _canvas;
        private Vector2 _pointerPosition;
        private bool _hasPointer;

        public void Bind(TopologyClientFlow flow)
        {
            Bind(flow, null);
        }

        /// <summary>由应用注入独立焦点系统；View 借用该实例，不在关闭时销毁它。</summary>
        public void Bind(TopologyClientFlow flow, EventSystem eventSystem)
        {
            if (_flow != null)
            {
                throw new InvalidOperationException("UI already bound.");
            }

            _flow = flow ?? throw new ArgumentNullException(nameof(flow));
            _canvas = GetComponent<Canvas>();
            _pointerSurfaces = GetComponentsInChildren<Graphic>(true);
            _loginNavigation = new Selectable[]
            {
                Username,
                Password,
                Login,
                Register,
                AdvancedButton,
                Host,
                Port,
                Tls,
                Pin,
            };
            _font = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei", "Microsoft YaHei UI", "Arial" },
                20
            );
            foreach (Text text in GetComponentsInChildren<Text>(true))
            {
                text.font = _font;
            }

            _eventSystem = eventSystem != null ? eventSystem : EventSystem.current;
            if (_eventSystem == null)
            {
                _ownedEventSystem = new GameObject(
                    "Local UI EventSystem",
                    typeof(MultiplayerEventSystem),
                    typeof(InputSystemUIInputModule)
                );
                _eventSystem = _ownedEventSystem.GetComponent<EventSystem>();
            }
            else
            {
                // 场景可以自行提供 EventSystem，保留其所有权；但不得
                // 让旧输入模块继续轮询项目已经禁用的输入后端。
                foreach (
                    StandaloneInputModule legacy in _eventSystem.GetComponents<StandaloneInputModule>()
                )
                {
                    legacy.enabled = false;
                }

                if (_eventSystem.GetComponent<InputSystemUIInputModule>() == null)
                {
                    _eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
                }
            }

            if (_eventSystem is MultiplayerEventSystem multiplayer)
            {
                multiplayer.playerRoot = gameObject;
            }

            foreach (ScopedInputField input in GetComponentsInChildren<ScopedInputField>(true))
            {
                input.ConfigureEventSystem(_eventSystem);
            }

            _viewModel = new TopologyViewModel(flow, value => GUIUtility.systemCopyBuffer = value);
            _viewModel.AdvancedVisible.Value = Advanced.activeSelf;
            _viewModel.DiagnosticsVisible.Value = DiagnosticPanel.activeSelf;
            _bindings = new BindingScope();
            BindInputsAndCommands();
            BindDisplayState();
            BindLists();
            Render();
        }

        private void BindInputsAndCommands()
        {
            _bindings.Bind(Username, _viewModel.Username);
            _bindings.Bind(Password, _viewModel.Password);
            _bindings.Bind(Host, _viewModel.Host);
            _bindings.Bind(Port, _viewModel.Port);
            _bindings.Bind(Pin, _viewModel.Pin);
            _bindings.Bind(Invite, _viewModel.Invite);
            _bindings.Bind(Tls, _viewModel.Tls);
            _bindings.Bind(Login, _viewModel.Login);
            _bindings.Bind(Register, _viewModel.Register);
            _bindings.Bind(Create, _viewModel.Create);
            _bindings.Bind(SendInvite, _viewModel.SendInvite);
            _bindings.Bind(Ready, _viewModel.Ready);
            _bindings.Bind(Match, _viewModel.Match);
            _bindings.Bind(Leave, _viewModel.Leave);
            _bindings.Bind(Cancel, _viewModel.Cancel);
            _bindings.Bind(Reconnect, _viewModel.Reconnect);
            _bindings.Bind(Back, _viewModel.Back);
            _bindings.Bind(AdvancedButton, _viewModel.ToggleAdvanced);
            _bindings.Bind(DiagnosticButton, _viewModel.ToggleDiagnostics);
            _bindings.Bind(CopyId, _viewModel.CopyId);
        }

        private void BindDisplayState()
        {
            _bindings.Bind(_viewModel.Page, ShowPage);
            _bindings.Bind(_viewModel.AdvancedVisible, Advanced.SetActive);
            _bindings.Bind(_viewModel.DiagnosticsVisible, DiagnosticPanel.SetActive);
            _bindings.Bind(_viewModel.NoticeVisible, Error.transform.parent.gameObject.SetActive);
            BindText(Error, _viewModel.Error);
            BindText(Status, _viewModel.Status);
            BindText(Player, _viewModel.Player);
            BindText(Statistics, _viewModel.Statistics);
            BindText(PartyHint, _viewModel.PartyHint);
            BindText(QueueTime, _viewModel.QueueTime);
            BindText(QueueMembers, _viewModel.QueueMembers);
            BindText(BattleVitals, _viewModel.BattleVitals);
            BindText(BattleClock, _viewModel.BattleClock);
            BindText(BattleConnection, _viewModel.BattleConnection);
            BindText(BattleKills, _viewModel.BattleKills);
            BindText(Diagnostics, _viewModel.Diagnostics);
            BindText(ResultTitle, _viewModel.ResultTitle);
            BindText(ResultStats, _viewModel.ResultStats);
            BindText(ResultPlayed, _viewModel.ResultPlayed);
            BindText(ResultWon, _viewModel.ResultWon);
            BindText(ResultKills, _viewModel.ResultKills);
            BindText(Ready.GetComponentInChildren<Text>(), _viewModel.ReadyLabel);
            _bindings.Bind(
                _viewModel.HealthFraction,
                value => HealthBar.rectTransform.anchorMax = new Vector2(value, 1)
            );
            _bindings.Bind(
                _viewModel.ArmorFraction,
                value => ArmorBar.rectTransform.anchorMax = new Vector2(value, 1)
            );
            _bindings.Bind(
                _viewModel.HasParty,
                hasParty =>
                {
                    Create.gameObject.SetActive(!hasParty);
                    Ready.gameObject.SetActive(hasParty);
                    Leave.gameObject.SetActive(hasParty);
                }
            );
        }

        private void BindLists()
        {
            _members = new KeyedViewList<string, TopologyViewModel.MemberRow, Text>(
                value => value.PlayerId,
                value =>
                {
                    Text row = Instantiate(MemberTemplate, MemberContent);
                    row.gameObject.SetActive(true);
                    return row;
                },
                (row, value, index) =>
                {
                    Set(row, value.Label);
                    row.transform.SetSiblingIndex(index);
                    row.color = value.Ready
                        ? new Color(.36f, .87f, .69f)
                        : new Color(.77f, .82f, .9f);
                },
                row =>
                {
                    if (row != null)
                    {
                        Destroy(row.gameObject);
                    }
                }
            );
            _invitations = new KeyedViewList<
                string,
                TopologyViewModel.InvitationRow,
                InvitationView
            >(
                value => value.PartyId,
                value =>
                {
                    var row = new InvitationView
                    {
                        Button = Instantiate(InvitationTemplate, InvitationContent),
                        Command = _viewModel.CreateInvitationCommand(value.PartyId),
                        Bindings = new BindingScope(),
                    };
                    row.Button.gameObject.SetActive(true);
                    row.Bindings.Bind(row.Button, row.Command);
                    return row;
                },
                (row, value, index) =>
                {
                    Set(row.Button.GetComponentInChildren<Text>(), value.Label);
                    row.Button.transform.SetSiblingIndex(index);
                    row.Command.RefreshCanExecute();
                },
                row => row.Dispose()
            );
            _bindings.Bind(_viewModel.Members, _members.Apply);
            _bindings.Bind(_viewModel.Invitations, _invitations.Apply);
        }

        private void BindText(Text text, BindableProperty<string> property)
        {
            _bindings.Bind(property, value => Set(text, value));
        }

        private void ShowPage(int page)
        {
            if (_page == page)
            {
                return;
            }
            for (int index = 0; index < Pages.Length; index++)
            {
                Pages[index].SetActive(index == page);
            }
            _page = page;
            if (_eventSystem != null)
            {
                _eventSystem.SetSelectedGameObject(null);
            }
        }

        // 键盘 Enter 是 Unity 输入入口；命令负责业务错误，入口观察非业务异常。
        private async void Authenticate(bool register)
        {
            try
            {
                await (register ? _viewModel.Register : _viewModel.Login).ExecuteAsync();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        private void Update()
        {
            if (_closed || _flow == null)
            {
                return;
            }

            Render();
            Pointer pointer = Pointer.current;
            _hasPointer = pointer != null;
            if (_hasPointer)
            {
                _pointerPosition = pointer.position.ReadValue();
            }

            RefreshInputBlock();
            Keyboard keyboard = Keyboard.current;
            GameObject selected =
                _eventSystem != null ? _eventSystem.currentSelectedGameObject : null;
            if (_page == 0 && _viewModel.Login.CanExecute && keyboard != null)
            {
                if (
                    (selected == Username.gameObject || selected == Password.gameObject)
                    && (
                        keyboard.enterKey.wasPressedThisFrame
                        || keyboard.numpadEnterKey.wasPressedThisFrame
                    )
                )
                {
                    Authenticate(false);
                }

                if (keyboard.tabKey.wasPressedThisFrame)
                {
                    int current = -1;
                    for (int i = 0; i < _loginNavigation.Length; i++)
                    {
                        if (_loginNavigation[i].gameObject == selected)
                        {
                            current = i;
                        }
                    }

                    int direction =
                        keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed
                            ? -1
                            : 1;
                    for (int step = 1; step <= _loginNavigation.Length; step++)
                    {
                        int index =
                            (current + direction * step + _loginNavigation.Length * 2)
                            % _loginNavigation.Length;
                        var next = _loginNavigation[index];
                        if (!next.gameObject.activeInHierarchy || !next.interactable)
                        {
                            continue;
                        }

                        if (_eventSystem != null)
                        {
                            _eventSystem.SetSelectedGameObject(next.gameObject);
                        }
                        if (next is InputField input)
                        {
                            input.ActivateInputField();
                        }

                        break;
                    }
                }
            }
        }

        private void LateUpdate()
        {
            if (_closed || _flow == null)
            {
                return;
            }

            // 保持小桌面窗口可读；高度不足时滚动，不继续缩小文字。
            _canvas.scaleFactor = Mathf.Max(.75f, _canvas.scaleFactor);
            for (int i = 0; i < ContentColumns.Length; i++)
            {
                var column = ContentColumns[i];
                float width = Mathf.Min(
                    ContentWidths[i],
                    ((RectTransform)column.parent).rect.width
                );
                if (!Mathf.Approximately(column.sizeDelta.x, width))
                {
                    column.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
                }
            }
        }

        /// <summary>使用上一渲染帧指针采样的 UI 输入保护，可在固定输入 Tick 前安全调用。</summary>
        public void RefreshInputBlock()
        {
            if (_closed || _flow == null)
            {
                return;
            }

            GameObject selected =
                _eventSystem != null ? _eventSystem.currentSelectedGameObject : null;
            InputField focused = selected != null ? selected.GetComponent<InputField>() : null;
            bool typing = focused != null && focused.isFocused;
            bool pointer = false;
            if (_page == 3 && _hasPointer)
            {
                foreach (Graphic surface in _pointerSurfaces)
                {
                    if (
                        surface != null
                        && surface.isActiveAndEnabled
                        && surface.raycastTarget
                        && RectTransformUtility.RectangleContainsScreenPoint(
                            surface.rectTransform,
                            _pointerPosition
                        )
                    )
                    {
                        pointer = true;
                        break;
                    }
                }
            }

            _flow.SetUiInputBlocked(typing || _page != 3, pointer || _page != 3);
        }

        private void Render()
        {
            _viewModel?.Refresh();
        }

        private static void Set(Text target, string value)
        {
            if (target.text != value)
            {
                target.text = value;
            }
        }

        /// <summary>显式关闭界面订阅和命令；幂等，保留外部对象及其监听所有权。</summary>
        public void Close()
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            if (_flow != null)
            {
                _flow.SetUiInputBlocked(false, false);
            }
            try
            {
                _bindings?.Dispose();
            }
            finally
            {
                _viewModel?.Dispose();
                _members?.Dispose();
                _invitations?.Dispose();
                if (_ownedEventSystem != null)
                {
                    Destroy(_ownedEventSystem);
                }
                if (_font != null)
                {
                    Destroy(_font);
                }
                _flow = null;
            }
        }

        private void OnDestroy()
        {
            Close();
        }
    }
}
