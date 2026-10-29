using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AiNative.Client.Core.Mvvm;
using AiNative.Client.Fantasy;

namespace AiNative.Client.Application
{
    /// <summary>
    /// 登录至结算的界面状态；由主线程刷新，不持有 Unity 控件。
    /// Flow 继续拥有网络请求与对局时序，关闭此模型只取消界面对操作的等待。
    /// </summary>
    public sealed class TopologyViewModel : IDisposable
    {
        public sealed class MemberRow
        {
            public string PlayerId { get; }
            public string Label { get; }
            public bool Ready { get; }

            public MemberRow(string playerId, string label, bool ready)
            {
                PlayerId = playerId;
                Label = label;
                Ready = ready;
            }
        }

        public sealed class InvitationRow
        {
            public string PartyId { get; }
            public string Label { get; }

            public InvitationRow(string partyId, string label)
            {
                PartyId = partyId;
                Label = label;
            }
        }

        private readonly TopologyClientFlow _flow;
        private readonly List<AsyncCommand> _commands = new List<AsyncCommand>();
        private GateParty _lastParty;
        private GateProfile _lastProfile;
        private IReadOnlyList<GateParty> _lastInvitations;
        private string _lastPlayer;
        private bool _lastBusy;
        private bool _ready;
        private bool _leader;
        private bool _allReady;
        private bool _disposed;

        public BindableProperty<string> Username { get; } = new BindableProperty<string>("");
        public BindableProperty<string> Password { get; } = new BindableProperty<string>("");
        public BindableProperty<string> Host { get; } = new BindableProperty<string>("");
        public BindableProperty<string> Port { get; } = new BindableProperty<string>("");
        public BindableProperty<string> Pin { get; } = new BindableProperty<string>("");
        public BindableProperty<string> Invite { get; } = new BindableProperty<string>("");
        public BindableProperty<bool> Tls { get; } = new BindableProperty<bool>();
        public BindableProperty<bool> AdvancedVisible { get; } = new BindableProperty<bool>();
        public BindableProperty<bool> DiagnosticsVisible { get; } = new BindableProperty<bool>();
        public BindableProperty<bool> HasParty { get; } = new BindableProperty<bool>();
        public BindableProperty<bool> NoticeVisible { get; } = new BindableProperty<bool>();
        public BindableProperty<int> Page { get; } = new BindableProperty<int>(-1);
        public BindableProperty<string> Error { get; } = new BindableProperty<string>("");
        public BindableProperty<string> Status { get; } = new BindableProperty<string>("");
        public BindableProperty<string> Player { get; } = new BindableProperty<string>("");
        public BindableProperty<string> Statistics { get; } = new BindableProperty<string>("");
        public BindableProperty<string> PartyHint { get; } = new BindableProperty<string>("");
        public BindableProperty<string> ReadyLabel { get; } = new BindableProperty<string>("准备");
        public BindableProperty<string> QueueTime { get; } = new BindableProperty<string>("");
        public BindableProperty<string> QueueMembers { get; } = new BindableProperty<string>("");
        public BindableProperty<string> BattleVitals { get; } = new BindableProperty<string>("");
        public BindableProperty<string> BattleClock { get; } = new BindableProperty<string>("");
        public BindableProperty<string> BattleConnection { get; } =
            new BindableProperty<string>("");
        public BindableProperty<string> BattleKills { get; } = new BindableProperty<string>("");
        public BindableProperty<float> HealthFraction { get; } = new BindableProperty<float>();
        public BindableProperty<float> ArmorFraction { get; } = new BindableProperty<float>();
        public BindableProperty<string> Diagnostics { get; } = new BindableProperty<string>("");
        public BindableProperty<string> ResultTitle { get; } = new BindableProperty<string>("");
        public BindableProperty<string> ResultStats { get; } = new BindableProperty<string>("");
        public BindableProperty<string> ResultPlayed { get; } = new BindableProperty<string>("");
        public BindableProperty<string> ResultWon { get; } = new BindableProperty<string>("");
        public BindableProperty<string> ResultKills { get; } = new BindableProperty<string>("");
        public BindableProperty<IReadOnlyList<MemberRow>> Members { get; } =
            new BindableProperty<IReadOnlyList<MemberRow>>(Array.Empty<MemberRow>());
        public BindableProperty<IReadOnlyList<InvitationRow>> Invitations { get; } =
            new BindableProperty<IReadOnlyList<InvitationRow>>(Array.Empty<InvitationRow>());

        public AsyncCommand Login { get; }
        public AsyncCommand Register { get; }
        public AsyncCommand Create { get; }
        public AsyncCommand SendInvite { get; }
        public AsyncCommand Ready { get; }
        public AsyncCommand Match { get; }
        public AsyncCommand Leave { get; }
        public AsyncCommand Cancel { get; }
        public AsyncCommand Reconnect { get; }
        public AsyncCommand Back { get; }
        public AsyncCommand ToggleAdvanced { get; }
        public AsyncCommand ToggleDiagnostics { get; }
        public AsyncCommand CopyId { get; }

        /// <summary>显式注入既有业务流程及剪贴板适配；不从全局容器查找依赖。</summary>
        public TopologyViewModel(TopologyClientFlow flow, Action<string> copyToClipboard)
        {
            _flow = flow ?? throw new ArgumentNullException(nameof(flow));
            if (copyToClipboard == null)
            {
                throw new ArgumentNullException(nameof(copyToClipboard));
            }
            Host.Value = flow.ConnectionHost;
            Port.Value = flow.ConnectionPort;
            Pin.Value = flow.ConnectionPin;
            Tls.Value = flow.ConnectionTls;
            Login = Business(() => Authenticate(false));
            Register = Business(() => Authenticate(true));
            Create = Business(flow.CreateParty);
            SendInvite = Business(() => flow.InvitePlayer(Invite.Value), () => _leader);
            Ready = Business(() => flow.SetReady(!_ready));
            Match = Business(flow.StartMatch, () => _leader && _allReady);
            Leave = Business(flow.LeaveParty);
            Cancel = Business(flow.CancelMatch);
            Reconnect = Business(flow.ReconnectBattle);
            Back = Business(flow.ReturnToLobby, () => flow.Completed && flow.SettlementConfirmed);
            ToggleAdvanced = Local(() => AdvancedVisible.Value = !AdvancedVisible.Value);
            ToggleDiagnostics = Local(() => DiagnosticsVisible.Value = !DiagnosticsVisible.Value);
            CopyId = Local(() => copyToClipboard(flow.PlayerId));
            Refresh();
        }

        /// <summary>邀请行持有返回命令，行释放时也须释放命令；模型关闭后禁止执行。</summary>
        public AsyncCommand CreateInvitationCommand(string partyId)
        {
            return new AsyncCommand(
                token => _flow.ExecuteUiActionAsync(() => _flow.AcceptInvitation(partyId), token),
                () => !_disposed && !_flow.UiBusy && _flow.Party == null
            );
        }

        /// <summary>每个渲染帧读取业务快照；相等属性不通知，成员列表仅在业务快照变化时重建。</summary>
        public void Refresh()
        {
            if (_disposed)
            {
                return;
            }
            int page = SelectPage();
            if (Page.Value == 0 && page != 0)
            {
                Password.Value = "";
            }
            Page.Value = page;
            Error.Value = _flow.DisplayError;
            bool waitingForRoom =
                _flow.Completed
                && _flow.SettlementConfirmed
                && _flow.BackendStatus == TopologyClientFlow.WaitingForRoomReleaseStatus;
            Status.Value =
                _flow.UiBusy && !waitingForRoom ? "正在请求服务器…" : _flow.BackendStatus;
            NoticeVisible.Value = Error.Value.Length > 0 || Status.Value.Length > 0;
            if (page == 1 || page == 2)
            {
                RefreshParty();
            }
            if (page == 2)
            {
                QueueTime.Value = FormatTime((int)Math.Floor(_flow.QueueElapsedSeconds));
            }
            if (page == 3)
            {
                RefreshBattle();
            }
            if (page == 4)
            {
                RefreshResult();
            }
            foreach (AsyncCommand command in _commands)
            {
                command.RefreshCanExecute();
            }
        }

        private int SelectPage()
        {
            if (_flow.AuthenticationRequired)
            {
                return 0;
            }
            if (_flow.Completed || _flow.State == "Settling" || _flow.State == "AwaitingResult")
            {
                return 4;
            }
            if (_flow.Battle != null)
            {
                return 3;
            }
            return _flow.State == "Queued" ? 2 : 1;
        }

        private void RefreshParty()
        {
            if (
                _lastPlayer == _flow.PlayerId
                && ReferenceEquals(_lastParty, _flow.Party)
                && ReferenceEquals(_lastProfile, _flow.Profile)
                && ReferenceEquals(_lastInvitations, _flow.Invitations)
                && _lastBusy == _flow.UiBusy
            )
            {
                return;
            }
            _lastPlayer = _flow.PlayerId;
            _lastParty = _flow.Party;
            _lastProfile = _flow.Profile;
            _lastInvitations = _flow.Invitations;
            _lastBusy = _flow.UiBusy;
            Player.Value = "玩家 ID   " + ShortId(_flow.PlayerId);
            GateProfile profile = _flow.Profile;
            Statistics.Value =
                profile == null
                    ? ""
                    : $"累计完成 {profile.Played} 局   /   胜利 {profile.Won}   /   击杀 {profile.Kills}";
            GateParty party = _flow.Party;
            HasParty.Value = party != null;
            _leader = party != null && party.LeaderId == _flow.PlayerId;
            _allReady = party != null && party.Members.Length > 0;
            _ready = false;
            var members = new List<MemberRow>();
            if (party != null)
            {
                foreach (GatePartyMember member in party.Members)
                {
                    string label =
                        $"玩家 {members.Count + 1:00}"
                        + (member.PlayerId == _flow.PlayerId ? "（我）" : "  ")
                        + ShortId(member.PlayerId)
                        + (member.PlayerId == party.LeaderId ? " [队长]" : "")
                        + (member.Ready ? "  已准备" : "  未准备");
                    members.Add(new MemberRow(member.PlayerId, label, member.Ready));
                    _allReady &= member.Ready;
                    if (member.PlayerId == _flow.PlayerId)
                    {
                        _ready = member.Ready;
                    }
                }
            }
            Members.Value = members;
            ReadyLabel.Value = _ready ? "取消准备" : "准备";
            if (party == null)
            {
                PartyHint.Value = "尚未组队：创建队伍，或接受好友邀请。";
                QueueMembers.Value = "正在同步队伍…";
            }
            else
            {
                if (!_allReady)
                {
                    PartyHint.Value = "请所有队员点击准备，队长即可开始匹配。";
                }
                else
                {
                    PartyHint.Value = _leader
                        ? "全员已准备，可以开始匹配。"
                        : "全员已准备，等待队长开始匹配。";
                }
                QueueMembers.Value =
                    $"{party.Members.Length} 位玩家一起匹配\n匹配成功后自动进入同一房间";
            }
            var invitations = new List<InvitationRow>();
            foreach (GateParty invitation in _flow.Invitations)
            {
                invitations.Add(
                    new InvitationRow(
                        invitation.PartyId,
                        "接受邀请  ·  队长 " + ShortId(invitation.LeaderId)
                    )
                );
            }
            Invitations.Value = invitations;
        }

        private void RefreshBattle()
        {
            BattleClientSession battle = _flow.Battle;
            BattleClock.Value = FormatTime((int)Math.Ceiling(battle.ArenaRemainingTicks / 60f));
            BattleConnection.Value =
                battle.State == BattleClientState.Active
                    ? "● 战斗连接正常"
                    : "战斗连接：" + battle.State;
            if (battle.TryGetArenaState(out var state))
            {
                BattleVitals.Value = $"生命 {state.Health}\n护甲 {state.Armor}";
                HealthFraction.Value = Math.Max(0, Math.Min(1, state.Health / 100f));
                ArmorFraction.Value = Math.Max(0, Math.Min(1, state.Armor / 100f));
                BattleKills.Value = $"击杀 {state.Kills}";
            }
            else
            {
                BattleVitals.Value = "正在同步战斗状态…";
            }
            if (DiagnosticsVisible.Value)
            {
                Diagnostics.Value =
                    $"房间  {_flow.RoomId}\nTick  {battle.LastReceivedTick}     ACK  {battle.LastAcknowledgedSequence}\n玩家  {_flow.PlayerId}";
            }
        }

        private void RefreshResult()
        {
            ResultTitle.Value = _flow.SettlementConfirmed
                ? "对局结束 · 已保存"
                : "对局结束 · 等待结算确认";
            ResultStats.Value =
                _flow.Battle != null && _flow.Battle.TryGetArenaState(out var state)
                    ? $"本局击杀  {state.Kills}\n\n"
                    : "";
            GateProfile profile = _flow.Profile;
            ResultPlayed.Value = profile == null ? "—" : profile.Played.ToString();
            ResultWon.Value = profile == null ? "—" : profile.Won.ToString();
            ResultKills.Value = profile == null ? "—" : profile.Kills.ToString();
        }

        private void Authenticate(bool register)
        {
            _flow.Authenticate(
                register,
                Username.Value,
                Password.Value,
                Host.Value,
                Port.Value,
                Tls.Value,
                Pin.Value
            );
        }

        private AsyncCommand Business(Action action, Func<bool> canExecute = null)
        {
            var command = new AsyncCommand(
                async token =>
                {
                    await _flow.ExecuteUiActionAsync(action, token);
                    Refresh();
                },
                () => !_disposed && !_flow.UiBusy && (canExecute == null || canExecute())
            );
            _commands.Add(command);
            return command;
        }

        private AsyncCommand Local(Action action)
        {
            var command = new AsyncCommand(
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    action();
                    return default;
                },
                () => !_disposed
            );
            _commands.Add(command);
            return command;
        }

        private static string FormatTime(int seconds) => $"{seconds / 60:00}:{seconds % 60:00}";

        private static string ShortId(string id) =>
            id.Length <= 12 ? id : id.Substring(0, 4) + "…" + id.Substring(id.Length - 4);

        /// <summary>解绑后调用；取消命令等待并清除密码，迟到业务结果不再触发刷新。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (AsyncCommand command in _commands)
            {
                command.Dispose();
            }
            _commands.Clear();
            Password.Value = "";
        }
    }
}
