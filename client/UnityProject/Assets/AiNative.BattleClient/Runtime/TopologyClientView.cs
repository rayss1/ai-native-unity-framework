using System;
using System.Collections.Generic;
using AiNative.Client.Fantasy;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace AiNative.Client.Application
{
    /// <summary>Application UI only. Network ownership remains in TopologyClientFlow.</summary>
    [DefaultExecutionOrder(-100)]
    public sealed class TopologyClientView : MonoBehaviour
    {
        [Header("Pages: login, lobby, queue, battle, result")]
        public GameObject[] Pages;
        public Text Status,
            Error,
            Player,
            Statistics,
            PartyHint,
            QueueTime,
            QueueMembers,
            BattleVitals,
            BattleClock,
            BattleConnection,
            Diagnostics,
            ResultTitle,
            ResultStats;
        public InputField Username,
            Password,
            Host,
            Port,
            Pin,
            Invite;
        public Toggle Tls;
        public GameObject Advanced,
            DiagnosticPanel;
        public Button Login,
            Register,
            AdvancedButton,
            CopyId,
            Create,
            SendInvite,
            Ready,
            Match,
            Leave,
            Cancel,
            Reconnect,
            DiagnosticButton,
            Back;
        public Transform MemberContent,
            InvitationContent;
        public Text MemberTemplate;
        public Button InvitationTemplate;
        public Image HealthBar,
            ArmorBar;
        public Text BattleKills,
            ResultPlayed,
            ResultWon,
            ResultKills;
        public RectTransform[] ContentColumns;
        public float[] ContentWidths;
        private TopologyClientFlow _flow;
        private readonly Dictionary<string, Text> _members = new Dictionary<string, Text>();
        private readonly Dictionary<string, Button> _invitations = new Dictionary<string, Button>();
        private readonly List<string> _remove = new List<string>();
        private GameObject _ownedEventSystem;
        private Font _font;
        private int _page = -1;
        private bool _ready;
        private int _lastQueueSecond = -1;
        private GateParty _lastParty;
        private GateProfile _lastProfile;
        private IReadOnlyList<GateParty> _lastInvitations;
        private bool _partyBusy;
        private string _lastPlayer;
        private readonly HashSet<string> _alive = new HashSet<string>();
        private Selectable[] _loginNavigation;
        private Graphic[] _pointerSurfaces;
        private Canvas _canvas;
        private Vector2 _pointerPosition;
        private bool _hasPointer;

        public void Bind(TopologyClientFlow flow)
        {
            if (_flow != null)
                throw new InvalidOperationException("UI already bound.");
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
                text.font = _font;
            if (EventSystem.current == null)
                _ownedEventSystem = new GameObject(
                    "Local UI EventSystem",
                    typeof(EventSystem),
                    typeof(InputSystemUIInputModule)
                );
            else
            {
                // A scene may supply its own EventSystem. Keep its ownership, but do not
                // leave a legacy module polling an input backend disabled by this project.
                foreach (
                    StandaloneInputModule legacy in EventSystem.current.GetComponents<StandaloneInputModule>()
                )
                    legacy.enabled = false;
                if (EventSystem.current.GetComponent<InputSystemUIInputModule>() == null)
                    EventSystem.current.gameObject.AddComponent<InputSystemUIInputModule>();
            }
            Host.text = flow.ConnectionHost;
            Port.text = flow.ConnectionPort;
            Pin.text = flow.ConnectionPin;
            Tls.isOn = flow.ConnectionTls;
            Login.onClick.AddListener(() => Authenticate(false));
            Register.onClick.AddListener(() => Authenticate(true));
            AdvancedButton.onClick.AddListener(() => Advanced.SetActive(!Advanced.activeSelf));
            CopyId.onClick.AddListener(() => GUIUtility.systemCopyBuffer = _flow.PlayerId);
            Create.onClick.AddListener(flow.CreateParty);
            SendInvite.onClick.AddListener(() => flow.InvitePlayer(Invite.text));
            Ready.onClick.AddListener(() => flow.SetReady(!_ready));
            Match.onClick.AddListener(flow.StartMatch);
            Leave.onClick.AddListener(flow.LeaveParty);
            Cancel.onClick.AddListener(flow.CancelMatch);
            Reconnect.onClick.AddListener(flow.ReconnectBattle);
            DiagnosticButton.onClick.AddListener(() =>
                DiagnosticPanel.SetActive(!DiagnosticPanel.activeSelf)
            );
            Back.onClick.AddListener(flow.ReturnToLobby);
            Render();
        }

        private void Authenticate(bool register)
        {
            if (_flow.UiBusy)
                return;
            _flow.Authenticate(
                register,
                Username.text,
                Password.text,
                Host.text,
                Port.text,
                Tls.isOn,
                Pin.text
            );
        }

        private void Update()
        {
            if (_flow == null)
                return;
            Render();
            Pointer pointer = Pointer.current;
            _hasPointer = pointer != null;
            if (_hasPointer)
                _pointerPosition = pointer.position.ReadValue();
            RefreshInputBlock();
            Keyboard keyboard = Keyboard.current;
            GameObject selected =
                EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (_page == 0 && !_flow.UiBusy && keyboard != null)
            {
                if (
                    (selected == Username.gameObject || selected == Password.gameObject)
                    && (
                        keyboard.enterKey.wasPressedThisFrame
                        || keyboard.numpadEnterKey.wasPressedThisFrame
                    )
                )
                    Authenticate(false);
                if (keyboard.tabKey.wasPressedThisFrame)
                {
                    int current = -1;
                    for (int i = 0; i < _loginNavigation.Length; i++)
                        if (_loginNavigation[i].gameObject == selected)
                            current = i;
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
                            continue;
                        next.Select();
                        if (next is InputField input)
                            input.ActivateInputField();
                        break;
                    }
                }
            }
        }

        private void LateUpdate()
        {
            if (_flow == null)
                return;
            // Keep small desktop windows readable. Shorter windows scroll instead of shrinking text further.
            _canvas.scaleFactor = Mathf.Max(.75f, _canvas.scaleFactor);
            for (int i = 0; i < ContentColumns.Length; i++)
            {
                var column = ContentColumns[i];
                float width = Mathf.Min(
                    ContentWidths[i],
                    ((RectTransform)column.parent).rect.width
                );
                if (!Mathf.Approximately(column.sizeDelta.x, width))
                    column.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            }
        }

        /// <summary>UI guard using the last render-frame pointer sample; safe before a fixed input tick.</summary>
        public void RefreshInputBlock()
        {
            if (_flow == null)
                return;
            GameObject selected =
                EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            InputField focused = selected != null ? selected.GetComponent<InputField>() : null;
            bool typing = focused != null && focused.isFocused;
            bool pointer = false;
            if (_page == 3 && _hasPointer)
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
            _flow.SetUiInputBlocked(typing || _page != 3, pointer || _page != 3);
        }

        private void Render()
        {
            int page =
                _flow.AuthenticationRequired ? 0
                : _flow.Completed || _flow.State == "Settling" || _flow.State == "AwaitingResult"
                    ? 4
                : _flow.Battle != null ? 3
                : _flow.State == "Queued" ? 2
                : 1;
            if (page != _page)
            {
                for (int i = 0; i < Pages.Length; i++)
                    Pages[i].SetActive(i == page);
                if (_page == 0 && page != 0)
                    Password.text = "";
                _page = page;
                if (EventSystem.current != null)
                    EventSystem.current.SetSelectedGameObject(null);
            }
            Set(Error, _flow.DisplayError);
            Set(Status, _flow.UiBusy ? "正在请求服务器…" : _flow.BackendStatus);
            Error.transform.parent.gameObject.SetActive(
                Error.text.Length > 0 || Status.text.Length > 0
            );
            Login.interactable = Register.interactable = !_flow.UiBusy;
            if (page == 1 || page == 2)
                RenderParty();
            if (page == 2)
            {
                int seconds = Mathf.FloorToInt(_flow.QueueElapsedSeconds);
                if (_lastQueueSecond != seconds)
                {
                    _lastQueueSecond = seconds;
                    Set(QueueTime, $"{seconds / 60:00}:{seconds % 60:00}");
                }
                Cancel.interactable = !_flow.UiBusy;
            }
            if (page == 3)
            {
                var battle = _flow.Battle;
                int seconds = Mathf.CeilToInt(battle.ArenaRemainingTicks / 60f);
                Set(BattleClock, $"{seconds / 60:00}:{seconds % 60:00}");
                Set(
                    BattleConnection,
                    battle.State == BattleClientState.Active
                        ? "● 战斗连接正常"
                        : "战斗连接：" + battle.State
                );
                if (battle.TryGetArenaState(out var state))
                {
                    Set(BattleVitals, $"生命 {state.Health}\n护甲 {state.Armor}");
                    HealthBar.rectTransform.anchorMax = new Vector2(
                        Mathf.Clamp01(state.Health / 100f),
                        1
                    );
                    ArmorBar.rectTransform.anchorMax = new Vector2(
                        Mathf.Clamp01(state.Armor / 100f),
                        1
                    );
                    Set(BattleKills, $"击杀 {state.Kills}");
                }
                else
                    Set(BattleVitals, "正在同步战斗状态…");
                if (DiagnosticPanel.activeSelf)
                    Set(
                        Diagnostics,
                        $"房间  {_flow.RoomId}\nTick  {battle.LastReceivedTick}     ACK  {battle.LastAcknowledgedSequence}\n玩家  {_flow.PlayerId}"
                    );
                Reconnect.interactable = !_flow.UiBusy;
            }
            if (page == 4)
            {
                Set(
                    ResultTitle,
                    _flow.SettlementConfirmed ? "对局结束 · 已保存" : "对局结束 · 等待结算确认"
                );
                var profile = _flow.Profile;
                string last =
                    _flow.Battle != null && _flow.Battle.TryGetArenaState(out var state)
                        ? $"本局击杀  {state.Kills}\n\n"
                        : "";
                Set(ResultStats, last);
                Set(ResultPlayed, profile == null ? "—" : profile.Played.ToString());
                Set(ResultWon, profile == null ? "—" : profile.Won.ToString());
                Set(ResultKills, profile == null ? "—" : profile.Kills.ToString());
                Back.interactable = _flow.Completed && _flow.SettlementConfirmed && !_flow.UiBusy;
            }
        }

        private void RenderParty()
        {
            if (
                _lastPlayer == _flow.PlayerId
                && ReferenceEquals(_lastParty, _flow.Party)
                && ReferenceEquals(_lastProfile, _flow.Profile)
                && ReferenceEquals(_lastInvitations, _flow.Invitations)
                && _partyBusy == _flow.UiBusy
            )
                return;
            _lastPlayer = _flow.PlayerId;
            _lastParty = _flow.Party;
            _lastProfile = _flow.Profile;
            _lastInvitations = _flow.Invitations;
            _partyBusy = _flow.UiBusy;
            Set(Player, "玩家 ID   " + ShortId(_flow.PlayerId));
            GateProfile profile = _flow.Profile;
            Set(
                Statistics,
                profile == null
                    ? ""
                    : $"累计完成 {profile.Played} 局   /   胜利 {profile.Won}   /   击杀 {profile.Kills}"
            );
            GateParty party = _flow.Party;
            bool leader = party != null && party.LeaderId == _flow.PlayerId;
            bool allReady = party != null && party.Members.Length > 0;
            _ready = false;
            var alive = _alive;
            alive.Clear();
            int ordinal = 0;
            if (party != null)
                foreach (GatePartyMember member in party.Members)
                {
                    ordinal++;
                    alive.Add(member.PlayerId);
                    if (!_members.TryGetValue(member.PlayerId, out Text row))
                    {
                        row = Instantiate(MemberTemplate, MemberContent);
                        row.gameObject.SetActive(true);
                        _members.Add(member.PlayerId, row);
                    }
                    Set(
                        row,
                        $"玩家 {ordinal:00}"
                            + (member.PlayerId == _flow.PlayerId ? "（我）" : "  ")
                            + ShortId(member.PlayerId)
                            + (member.PlayerId == party.LeaderId ? " [队长]" : "")
                            + (member.Ready ? "  已准备" : "  未准备")
                    );
                    row.color = member.Ready
                        ? new Color(.36f, .87f, .69f)
                        : new Color(.77f, .82f, .9f);
                    allReady &= member.Ready;
                    if (member.PlayerId == _flow.PlayerId)
                        _ready = member.Ready;
                }
            _remove.Clear();
            foreach (string key in _members.Keys)
                if (!alive.Contains(key))
                    _remove.Add(key);
            foreach (string key in _remove)
            {
                Destroy(_members[key].gameObject);
                _members.Remove(key);
            }
            Set(
                PartyHint,
                party == null ? "尚未组队：创建队伍，或接受好友邀请。"
                    : !allReady ? "请所有队员点击准备，队长即可开始匹配。"
                    : leader ? "全员已准备，可以开始匹配。"
                    : "全员已准备，等待队长开始匹配。"
            );
            Set(
                QueueMembers,
                party == null
                    ? "正在同步队伍…"
                    : $"{party.Members.Length} 位玩家一起匹配\n匹配成功后自动进入同一房间"
            );
            Set(Ready.GetComponentInChildren<Text>(), _ready ? "取消准备" : "准备");
            Create.gameObject.SetActive(party == null);
            Ready.gameObject.SetActive(party != null);
            Leave.gameObject.SetActive(party != null);
            Create.interactable = Ready.interactable = Leave.interactable = !_flow.UiBusy;
            Match.interactable = leader && allReady && !_flow.UiBusy;
            SendInvite.interactable = leader && !_flow.UiBusy;
            alive.Clear();
            foreach (GateParty invitation in _flow.Invitations)
            {
                alive.Add(invitation.PartyId);
                if (!_invitations.TryGetValue(invitation.PartyId, out Button row))
                {
                    row = Instantiate(InvitationTemplate, InvitationContent);
                    row.gameObject.SetActive(true);
                    string id = invitation.PartyId;
                    row.onClick.AddListener(() => _flow.AcceptInvitation(id));
                    _invitations.Add(id, row);
                }
                Set(
                    row.GetComponentInChildren<Text>(),
                    "接受邀请  ·  队长 " + ShortId(invitation.LeaderId)
                );
                row.interactable = !_flow.UiBusy && party == null;
            }
            _remove.Clear();
            foreach (string key in _invitations.Keys)
                if (!alive.Contains(key))
                    _remove.Add(key);
            foreach (string key in _remove)
            {
                Destroy(_invitations[key].gameObject);
                _invitations.Remove(key);
            }
        }

        private static void Set(Text target, string value)
        {
            if (target.text != value)
                target.text = value;
        }

        private static string ShortId(string id) =>
            id.Length <= 12 ? id : id.Substring(0, 4) + "…" + id.Substring(id.Length - 4);

        private void OnDestroy()
        {
            if (_flow != null)
                _flow.SetUiInputBlocked(false, false);
            foreach (Button button in GetComponentsInChildren<Button>(true))
                button.onClick.RemoveAllListeners();
            if (_ownedEventSystem != null)
                Destroy(_ownedEventSystem);
            if (_font != null)
                Destroy(_font);
            _flow = null;
        }
    }
}
