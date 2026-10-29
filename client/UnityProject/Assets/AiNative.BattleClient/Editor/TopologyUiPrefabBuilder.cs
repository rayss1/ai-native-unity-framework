using AiNative.Client.Application;
using AiNative.Client.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace AiNative.Client.Editor
{
    /// <summary>通过 Unity 序列化创建可编辑的 uGUI 资产，不手写 YAML。</summary>
    public static class TopologyUiPrefabBuilder
    {
        private static readonly Color _ink = new Color(.055f, .073f, .105f, 1);
        private static readonly Color _panelColor = new Color(.085f, .115f, .16f, .98f);
        private static readonly Color _accent = new Color(57f / 255, 124f / 255, 246f / 255);
        private static Font _font;
        private static readonly System.Collections.Generic.List<RectTransform> _columns =
            new System.Collections.Generic.List<RectTransform>();
        private static readonly System.Collections.Generic.List<float> _widths =
            new System.Collections.Generic.List<float>();

        [MenuItem("AI Native/Local Test/Rebuild UGUI Prefab")]
        public static void Build()
        {
            const string directory = "Assets/AiNative.BattleClient/Resources";
            if (!AssetDatabase.IsValidFolder(directory))
            {
                AssetDatabase.CreateFolder("Assets/AiNative.BattleClient", "Resources");
            }

            _columns.Clear();
            _widths.Clear();
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var root = new GameObject(
                "TopologyClientUI",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster),
                typeof(TopologyClientView)
            );
            try
            {
                root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var scale = root.GetComponent<CanvasScaler>();
                scale.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scale.referenceResolution = new Vector2(1280, 720);
                scale.matchWidthOrHeight = .5f;
                var view = root.GetComponent<TopologyClientView>();
                view.Pages = new GameObject[5];
                for (int i = 0; i < 5; i++)
                {
                    var page = Box(
                        new[] { "LoginPage", "LobbyPage", "QueuePage", "BattlePage", "ResultPage" }[
                            i
                        ],
                        root.transform,
                        i == 3 ? (Color?)null : _ink
                    );
                    Stretch(page);
                    view.Pages[i] = page.gameObject;
                }

                BuildLogin(view);
                BuildLobby(view);
                BuildQueue(view);
                BuildBattle(view);
                BuildResult(view);
                view.ContentColumns = _columns.ToArray();
                view.ContentWidths = _widths.ToArray();
                var notification = Box(
                    "Notifications",
                    root.transform,
                    new Color(.05f, .07f, .1f, .92f)
                );
                notification.anchorMin = new Vector2(.05f, 0);
                notification.anchorMax = new Vector2(.95f, 0);
                notification.pivot = new Vector2(.5f, 0);
                notification.anchoredPosition = new Vector2(0, 8);
                notification.sizeDelta = new Vector2(0, 102);
                Layout(notification, 4, 8);
                view.Status = Label("Status", notification, "", 17, 22);
                view.Error = Label("Error", notification, "", 17, 60);
                view.Error.color = new Color(1, .55f, .5f);
                // 战斗通知的空白文本区域不得消耗指针输入。
                notification.GetComponent<Image>().raycastTarget = false;
                for (int i = 1; i < view.Pages.Length; i++)
                {
                    view.Pages[i].SetActive(false);
                }

                PrefabUtility.SaveAsPrefabAsset(root, directory + "/TopologyClientUI.prefab");
                AssetDatabase.SaveAssets();
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// 迁移已有 Prefab 的输入框脚本，保留 GameObject、组件 fileID 和序列化字段。
        /// 仅用于原生 InputField 到兼容派生类型的迁移；保存并重新导入后检查引用。
        /// </summary>
        [MenuItem("AI Native/Local Test/Migrate Existing UI Input Fields")]
        public static void MigrateExistingInputFields()
        {
            const string prefabPath =
                "Assets/AiNative.BattleClient/Resources/TopologyClientUI.prefab";
            const string scriptPath =
                "Packages/com.ainative.client.ui.ugui/Runtime/ScopedInputField.cs";
            MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
            if (script == null || script.GetClass() != typeof(ScopedInputField))
            {
                throw new System.InvalidOperationException(
                    "ScopedInputField script is not compiled."
                );
            }
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                throw new System.InvalidOperationException("Existing UI prefab is missing.");
            }
            TopologyClientView view = prefab.GetComponent<TopologyClientView>();
            if (view == null)
            {
                throw new System.InvalidOperationException("Existing UI view is missing.");
            }
            string[] fieldNames = { "Username", "Password", "Host", "Port", "Pin", "Invite" };
            var fieldIds = new System.Collections.Generic.Dictionary<string, long>();
            var originalView = new SerializedObject(view);
            foreach (string fieldName in fieldNames)
            {
                Object reference = originalView.FindProperty(fieldName).objectReferenceValue;
                if (
                    !(reference is InputField)
                    || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                        reference,
                        out string _,
                        out long id
                    )
                )
                {
                    throw new System.InvalidOperationException(
                        "Invalid input reference: " + fieldName
                    );
                }
                fieldIds.Add(fieldName, id);
            }
            InputField[] inputs = prefab.GetComponentsInChildren<InputField>(true);
            // 先验证全部目标，避免不兼容派生类型被覆盖后才发现错误。
            foreach (InputField input in inputs)
            {
                if (
                    input.GetType() != typeof(InputField)
                    && input.GetType() != typeof(ScopedInputField)
                )
                {
                    throw new System.InvalidOperationException(
                        "Unsupported input subtype: " + input.GetType()
                    );
                }
            }
            int expectedInputCount = inputs.Length;
            int migrated = 0;
            for (int index = 0; index < expectedInputCount; index++)
            {
                // 替换脚本会使旧托管 wrapper 失效，每一步重新获取当前资产和组件。
                GameObject currentPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                InputField target = null;
                foreach (
                    InputField candidate in currentPrefab.GetComponentsInChildren<InputField>(true)
                )
                {
                    if (candidate.GetType() == typeof(InputField))
                    {
                        target = candidate;
                        break;
                    }
                }
                if (target == null)
                {
                    break;
                }
                var serializedInput = new SerializedObject(target);
                SerializedProperty scriptProperty = serializedInput.FindProperty("m_Script");
                if (scriptProperty == null)
                {
                    throw new System.InvalidOperationException(
                        "InputField script property is missing."
                    );
                }
                scriptProperty.objectReferenceValue = script;
                // Apply 自行标记序列化改动；之后不得再访问 target 或旧属性 wrapper。
                serializedInput.ApplyModifiedPropertiesWithoutUndo();
                migrated++;
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
            GameObject imported = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var importedView = new SerializedObject(imported.GetComponent<TopologyClientView>());
            foreach (string fieldName in fieldNames)
            {
                Object reference = importedView.FindProperty(fieldName).objectReferenceValue;
                if (
                    !(reference is ScopedInputField)
                    || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                        reference,
                        out string _,
                        out long id
                    )
                    || id != fieldIds[fieldName]
                )
                {
                    throw new System.InvalidOperationException(
                        "Input migration failed validation: " + fieldName
                    );
                }
            }
            if (
                imported.GetComponentsInChildren<ScopedInputField>(true).Length
                != expectedInputCount
            )
            {
                throw new System.InvalidOperationException(
                    "Input migration changed component count."
                );
            }
            Debug.Log(
                "Migrated UI input scripts: "
                    + migrated
                    + "; verified original View references and fileIDs."
            );
        }

        private static void BuildLogin(TopologyClientView v)
        {
            var content = PageContent(v.Pages[0].transform, "欢迎进入对战", "本机对战", 620);
            content.gameObject.AddComponent<Image>().color = _panelColor;
            Label("Intro", content, "使用不同账号，在多个客户端中一起进入对局。", 20, 35);
            Label("UsernameLabel", content, "账号 · 3–32 位字母、数字或下划线", 18, 28);
            v.Username = Field("Username", content, "请输入账号");
            Label("PasswordLabel", content, "密码 · 6–32 位", 18, 28);
            v.Password = Field("Password", content, "请输入密码", true);
            Label("Privacy", content, "密码仅在本次会话中使用，不写入本机文件。", 16, 26);
            var row = Row("Authentication", content);
            v.Login = Button("Login", row, "登录", true);
            v.Register = Button("Register", row, "注册新账号");
            v.AdvancedButton = Button("AdvancedButton", content, "连接设置  ▾");
            var advanced = Box("Advanced", content, _panelColor);
            Layout(advanced, 8, 12);
            Fit(advanced);
            v.Advanced = advanced.gameObject;
            v.Host = Field("Host", advanced, "服务器地址");
            v.Port = Field("Port", advanced, "端口");
            v.Tls = Toggle("TLS", advanced, "使用 TLS 加密连接");
            v.Pin = Field("Pin", advanced, "证书 SHA256（可选）");
            advanced.gameObject.SetActive(false);
        }

        private static void BuildLobby(TopologyClientView v)
        {
            var c = PageContent(v.Pages[1].transform, "", "", 1180);
            var identity = Row("Identity", c, 48);
            var title = Label("Title", identity, "对战大厅", 34, 48);
            Width(title.gameObject, 220);
            v.Player = Label("PlayerId", identity, "玩家 ID", 18, 48);
            Flexible(v.Player.gameObject);
            v.CopyId = Button("CopyId", identity, "复制完整 ID");
            Width(v.CopyId.gameObject, 170);
            v.Statistics = Label("Statistics", c, "累计战绩", 18, 28);
            var columns = Row("LobbyColumns", c, 320);
            var team = Box("TeamPanel", columns, _panelColor);
            Layout(team, 6, 16);
            Element(team.gameObject).flexibleWidth = 1.5f;
            Label("TeamTitle", team, "我的队伍", 26, 42);
            var members = Box("Members", team, null);
            Layout(members, 6, 0);
            Fit(members);
            v.MemberContent = members;
            v.MemberTemplate = Label("MemberTemplate", members, "玩家", 18, 42);
            v.MemberTemplate.gameObject.SetActive(false);
            var invitationPanel = Box("InvitePanel", columns, _panelColor);
            Layout(invitationPanel, 8, 16);
            Element(invitationPanel.gameObject).flexibleWidth = 1;
            Label("InviteTitle", invitationPanel, "邀请玩家", 26, 42);
            v.Invite = Field("PlayerId", invitationPanel, "粘贴好友的玩家 ID");
            v.SendInvite = Button("SendInvite", invitationPanel, "发送邀请", true);
            Label("InvitationsTitle", invitationPanel, "收到的邀请（离队后可接受）", 16, 28);
            var invitations = Box("Invitations", invitationPanel, null);
            Layout(invitations, 5, 0);
            Fit(invitations);
            v.InvitationContent = invitations;
            v.InvitationTemplate = Button("InvitationTemplate", invitations, "接受邀请");
            v.InvitationTemplate.gameObject.SetActive(false);
            v.PartyHint = Label("PartyHint", c, "创建队伍或接受邀请", 18, 30);
            var controls = Row("PartyActions", c);
            v.Leave = Button("Leave", controls, "退出队伍");
            v.Create = Button("Create", controls, "创建队伍", true);
            v.Ready = Button("Ready", controls, "准备", true);
            v.Match = Button("Match", controls, "开始匹配", true);
        }

        private static void BuildQueue(TopologyClientView v)
        {
            var c = PageContent(
                v.Pages[2].transform,
                "正在寻找对局",
                "MATCHMAKING  /  匹配中",
                700
            );
            v.QueueTime = Label("Timer", c, "00:00", 64, 110);
            v.QueueTime.alignment = TextAnchor.MiddleCenter;
            v.QueueMembers = Label("Members", c, "正在同步队伍", 23, 100);
            v.QueueMembers.alignment = TextAnchor.MiddleCenter;
            Label("Hint", c, "保持窗口运行，匹配成功后自动进入战斗。", 20, 55);
            v.Cancel = Button("Cancel", c, "取消匹配");
            Label("CancellationHint", c, "取消操作需等待服务器确认。", 16, 30);
        }

        private static void BuildBattle(TopologyClientView v)
        {
            Transform page = v.Pages[3].transform;
            var upper = Box("VitalsPanel", page, _panelColor);
            Corner(upper, new Vector2(0, 1), new Vector2(20, -20), new Vector2(280, 84));
            v.BattleVitals = Label("Vitals", upper, "生命 —\n护甲 —", 22, 70);
            Stretch(v.BattleVitals.rectTransform, 10);
            v.BattleVitals.rectTransform.offsetMax = new Vector2(-160, -10);
            v.HealthBar = Bar("Health", upper, new Vector2(120, -18), new Color(.32f, .8f, .25f));
            v.ArmorBar = Bar("Armor", upper, new Vector2(120, -50), _accent);
            var clock = Box("Clock", page, _panelColor);
            Corner(clock, new Vector2(.5f, 1), new Vector2(0, -20), new Vector2(120, 64));
            v.BattleClock = Label("Time", clock, "对战中", 30, 60);
            Stretch(v.BattleClock.rectTransform);
            v.BattleClock.alignment = TextAnchor.MiddleCenter;
            var actions = Box("TopRight", page, _panelColor);
            Corner(actions, new Vector2(1, 1), new Vector2(-20, -20), new Vector2(340, 64));
            var actionRow = Row("Actions", actions);
            Stretch(actionRow, 8);
            v.BattleKills = Label("Kills", actionRow, "击杀 0", 20, 42);
            Width(v.BattleKills.gameObject, 65);
            v.BattleConnection = Label("Connection", actionRow, "连接中", 17, 42);
            Flexible(v.BattleConnection.gameObject);
            v.DiagnosticButton = Button("DiagnosticsButton", actionRow, "诊断 ▾");
            Width(v.DiagnosticButton.gameObject, 90);
            var diagnostic = Box("DiagnosticsPanel", page, _panelColor);
            Corner(diagnostic, new Vector2(1, 1), new Vector2(-20, -100), new Vector2(500, 155));
            v.DiagnosticPanel = diagnostic.gameObject;
            v.Diagnostics = Label("Diagnostics", diagnostic, "", 16, 145);
            Stretch(v.Diagnostics.rectTransform, 12);
            diagnostic.gameObject.SetActive(false);
            var weapon = Box("WeaponPanel", page, _panelColor);
            Corner(weapon, new Vector2(1, 0), new Vector2(-20, 94), new Vector2(330, 75));
            var weaponRow = Row("Weapon", weapon, 54);
            Stretch(weaponRow, 10);
            var weaponText = Label("WeaponLabel", weaponRow, "当前武器\n机枪", 20, 54);
            Flexible(weaponText.gameObject);
            v.Reconnect = Button("Reconnect", weaponRow, "重新连接");
            Width(v.Reconnect.gameObject, 140);
            var hint = Label(
                "ControlsHint",
                page,
                "WASD 移动 · 鼠标左键开火\n切窗或操作界面时暂停输入",
                18,
                60
            );
            Corner(
                hint.rectTransform,
                new Vector2(0, 0),
                new Vector2(20, 100),
                new Vector2(390, 60)
            );
        }

        private static Image Bar(string name, Transform parent, Vector2 offset, Color color)
        {
            var background = Box(name + "Background", parent, new Color(.19f, .23f, .29f));
            Corner(background, new Vector2(0, 1), offset, new Vector2(145, 16));
            var fill = Box(name + "Fill", background, color);
            Stretch(fill);
            fill.GetComponent<Image>().raycastTarget = false;
            return fill.GetComponent<Image>();
        }

        private static void BuildResult(TopologyClientView v)
        {
            var c = PageContent(v.Pages[4].transform, "对局结果", "MATCH COMPLETE", 820);
            c.gameObject.AddComponent<Image>().color = _panelColor;
            v.ResultTitle = Label("ResultTitle", c, "等待结算确认", 27, 54);
            v.ResultTitle.alignment = TextAnchor.MiddleCenter;
            v.ResultStats = Label("LocalKills", c, "", 22, 38);
            v.ResultStats.alignment = TextAnchor.MiddleCenter;
            Label("StatsTitle", c, "累计战绩", 23, 38);
            var cards = Row("Statistics", c, 132);
            v.ResultPlayed = StatCard(cards, "完成局数");
            v.ResultWon = StatCard(cards, "胜利场次");
            v.ResultKills = StatCard(cards, "累计击杀");
            v.Back = Button("Back", c, "返回大厅", true);
            Label(
                "SaveHint",
                c,
                "服务器确认保存后可返回大厅。保留登录，队员需要重新准备。",
                18,
                55
            );
        }

        private static Text StatCard(Transform parent, string caption)
        {
            var card = Box(caption, parent, new Color(.12f, .16f, .22f));
            Flexible(card.gameObject);
            Layout(card, 4, 12);
            var label = Label("Caption", card, caption, 19, 32);
            label.alignment = TextAnchor.MiddleCenter;
            var value = Label("Value", card, "—", 42, 64);
            value.alignment = TextAnchor.MiddleCenter;
            return value;
        }

        private static RectTransform PageContent(
            Transform page,
            string title,
            string subtitle,
            float width
        )
        {
            var viewport = Box("Viewport", page, Color.clear);
            Stretch(viewport);
            viewport.offsetMin = new Vector2(32, 112);
            viewport.offsetMax = new Vector2(-32, -20);
            viewport.gameObject.AddComponent<RectMask2D>();
            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 35;
            var c = Box("Content", viewport, null);
            c.anchorMin = new Vector2(.5f, 1);
            c.anchorMax = new Vector2(.5f, 1);
            c.pivot = new Vector2(.5f, 1);
            c.sizeDelta = new Vector2(width, 0);
            _columns.Add(c);
            _widths.Add(width);
            Layout(c, 8, 16);
            Fit(c);
            scroll.content = c;
            scroll.viewport = viewport;
            if (subtitle.Length > 0)
            {
                Label("Subtitle", c, subtitle, 17, 32).color = _accent;
            }

            if (title.Length > 0)
            {
                Label("Title", c, title, 38, 62);
            }

            return c;
        }

        private static RectTransform Box(string name, Transform parent, Color? color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            if (color.HasValue)
            {
                go.AddComponent<Image>().color = color.Value;
            }

            return go.GetComponent<RectTransform>();
        }

        private static Text Label(
            string name,
            Transform parent,
            string value,
            int size,
            float height
        )
        {
            var rt = Box(name, parent, null);
            var text = rt.gameObject.AddComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.text = value;
            text.color = new Color(.88f, .92f, .97f);
            text.alignment = TextAnchor.MiddleLeft;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.supportRichText = false;
            Height(rt.gameObject, height);
            return text;
        }

        private static Button Button(
            string name,
            Transform parent,
            string value,
            bool primary = false
        )
        {
            var rt = Box(name, parent, primary ? _accent : new Color(.16f, .21f, .29f));
            Height(rt.gameObject, 46);
            Flexible(rt.gameObject);
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = rt.GetComponent<Image>();
            var colors = button.colors;
            colors.disabledColor = new Color(.38f, .4f, .43f, .65f);
            button.colors = colors;
            var label = Label("Label", rt, value, 20, 42);
            Stretch(label.rectTransform, 8);
            label.alignment = TextAnchor.MiddleCenter;
            return button;
        }

        private static InputField Field(
            string name,
            Transform parent,
            string placeholder,
            bool password = false
        )
        {
            var rt = Box(name, parent, new Color(.035f, .055f, .083f));
            Height(rt.gameObject, 48);
            var field = rt.gameObject.AddComponent<ScopedInputField>();
            field.targetGraphic = rt.GetComponent<Image>();
            field.characterLimit = password ? 32 : 256;
            var text = Label("Text", rt, "", 20, 44);
            Stretch(text.rectTransform, 12);
            field.textComponent = text;
            var hint = Label("Placeholder", rt, placeholder, 18, 44);
            Stretch(hint.rectTransform, 12);
            hint.color = new Color(.5f, .57f, .67f);
            field.placeholder = hint;
            field.contentType = password
                ? InputField.ContentType.Password
                : InputField.ContentType.Standard;
            return field;
        }

        private static Toggle Toggle(string name, Transform parent, string text)
        {
            var rt = Box(name, parent, null);
            Height(rt.gameObject, 36);
            var toggle = rt.gameObject.AddComponent<Toggle>();
            var box = Box("Box", rt, new Color(.2f, .25f, .32f));
            Corner(box, new Vector2(0, .5f), new Vector2(14, 0), new Vector2(24, 24));
            var check = Box("Check", box, _accent);
            Stretch(check, 4);
            toggle.targetGraphic = box.GetComponent<Image>();
            toggle.graphic = check.GetComponent<Image>();
            var label = Label("Label", rt, text, 18, 36);
            Stretch(label.rectTransform);
            label.rectTransform.offsetMin = new Vector2(38, 0);
            return toggle;
        }

        private static RectTransform Row(string name, Transform parent, float height = 48)
        {
            var rt = Box(name, parent, null);
            Height(rt.gameObject, height);
            var layout = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 12;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            return rt;
        }

        private static void Layout(RectTransform rt, int gap, int padding)
        {
            var l = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            l.spacing = gap;
            l.padding = new RectOffset(padding, padding, padding, padding);
            l.childControlHeight = true;
            l.childControlWidth = true;
            l.childForceExpandHeight = false;
        }

        private static void Fit(RectTransform rt)
        {
            rt.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter
                .FitMode
                .PreferredSize;
        }

        private static LayoutElement Element(GameObject go)
        {
            return go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        }

        private static void Height(GameObject go, float h)
        {
            Element(go).minHeight = h;
            Element(go).preferredHeight = h;
        }

        private static void Width(GameObject go, float w)
        {
            Element(go).minWidth = w;
            Element(go).preferredWidth = w;
            Element(go).flexibleWidth = 0;
        }

        private static void Flexible(GameObject go)
        {
            Element(go).flexibleWidth = 1;
        }

        private static void Stretch(RectTransform rt, float padding = 0)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.one * padding;
            rt.offsetMax = -Vector2.one * padding;
        }

        private static void Corner(RectTransform rt, Vector2 anchor, Vector2 offset, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = offset;
            rt.sizeDelta = size;
        }
    }
}
