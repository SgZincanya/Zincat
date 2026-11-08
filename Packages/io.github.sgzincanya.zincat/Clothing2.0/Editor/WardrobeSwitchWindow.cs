#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Zincat.Clothing.Editor
{
    /// <summary>向现有换衣预留项注入 MA Object Toggle，并引用原分件菜单建立总菜单。</summary>
    public sealed class WardrobeSwitchWindow : EditorWindow
    {
        private static Color Background => EditorGUIUtility.isProSkin ? new Color32(52, 52, 52, 255) : new Color(.95f, .95f, .96f);
        private static Color Panel => EditorGUIUtility.isProSkin ? new Color32(58, 58, 58, 255) : Color.white;
        private static Color Accent => new Color(.10f, .48f, .96f);
        private static Color Muted => WardrobeUiStyle.Muted;
        private static Color Border => EditorGUIUtility.isProSkin ? new Color(.27f, .28f, .30f) : new Color(.84f, .85f, .87f);
        private static readonly Type InstallTargetType = typeof(ModularAvatarMenuInstaller).Assembly
            .GetType("nadena.dev.modular_avatar.core.ModularAvatarMenuInstallTarget");

        [SerializeField] private VRCAvatarDescriptor avatar;
        [SerializeField] private List<GameObject> switchSlots = new List<GameObject>();
        [SerializeField] private GameObject defaultSlot;
        [SerializeField] private bool usePlaceholderOutfit;
        [SerializeField] private GameObject placeholderSlot;
        [SerializeField] private GameObject placeholderOutfit;
        [SerializeField] private string sharedParameter;
        [SerializeField] private string collectionMenuName = "衣服菜单";
        [SerializeField] private bool showAdvanced;
        [SerializeField] private WardrobeGeneratedSwitch loadedCollection;
        [SerializeField] private string loadedCollectionName;

        private VisualElement slotRows;
        private Label status;
        private Button generateButton;
        private Button generateAllIconsButton;
        private bool iconCaptureBusy;
        private TextField parameterField;
        private TextField collectionMenuNameField;
        private HelpBox feedback;

        [MenuItem("Zincat/衣服菜单/衣服切换")]
        private static void Open()
        {
            var window = GetWindow<WardrobeSwitchWindow>("衣服切换");
            window.minSize = new Vector2(620, 510);
            window.Show();
        }

        private void OnEnable()
        {
            if (switchSlots == null) switchSlots = new List<GameObject>();
        }

        public void CreateGUI()
        {
            if (avatar == null)
                avatar = Selection.activeGameObject?.GetComponentInParent<VRCAvatarDescriptor>();
            string message = null;
            if (avatar != null && switchSlots.Count == 0) TryLoadAvatarCollection(out message);
            else if (avatar != null && loadedCollection == null)
            {
                var groups = AvatarCollections();
                if (groups.Count == 1)
                {
                    // 旧窗口有未保存选择时只绑定目标，不重新读取覆盖窗口草稿。
                    loadedCollection = groups[0];
                    if (string.IsNullOrWhiteSpace(collectionMenuName) || collectionMenuName == "衣服菜单")
                        collectionMenuName = loadedCollection.gameObject.name;
                    loadedCollectionName = loadedCollection.gameObject.name;
                    if (string.IsNullOrWhiteSpace(sharedParameter)) sharedParameter = loadedCollection.ParameterName;
                }
            }
            BuildUI();
            if (!string.IsNullOrEmpty(message)) SetFeedback(message);
        }

        private void BuildUI()
        {
            if (placeholderOutfit == null && placeholderSlot != null)
                placeholderOutfit = placeholderSlot.transform.parent?.GetComponent<WardrobeGeneratedMenu>()?.ClothingRoot;
            var root = rootVisualElement;
            root.Clear();
            root.style.flexGrow = 1;
            root.style.backgroundColor = Background;
            root.style.paddingLeft = 18;
            root.style.paddingRight = 18;
            root.style.paddingTop = 14;
            root.style.paddingBottom = 14;
            root.style.unityFontStyleAndWeight = FontStyle.Normal;

            var title = new Label("衣服切换");
            title.style.fontSize = 20;
            title.style.marginBottom = 6;
            root.Add(title);
            WardrobeDiagnostics.Attach(root, "衣柜", DiagnosticState);
            var help = new Label("添加衣服，选好默认穿着，再生成换衣菜单。每次只穿一套，分件设置保持不变。");
            help.style.whiteSpace = WhiteSpace.Normal;
            help.style.color = Muted;
            help.style.marginBottom = 10;
            root.Add(help);

            var avatarField = new ObjectField("当前角色")
            {
                objectType = typeof(VRCAvatarDescriptor),
                allowSceneObjects = true,
                value = avatar
            };
            avatarField.style.marginBottom = 9;
            avatarField.tooltip = "添加衣服后自动识别角色（Avatar），也可以手动选择。";
            avatarField.RegisterValueChangedCallback(evt => SetAvatar(evt.newValue as VRCAvatarDescriptor));
            var roleLabel = new Label("当前角色：" + (avatar == null ? "添加衣服后自动识别" : avatar.name));
            roleLabel.style.color = Muted;
            roleLabel.style.marginBottom = 10;
            root.Add(roleLabel);

            var drop = new VisualElement();
            drop.style.minHeight = 128;
            drop.style.justifyContent = Justify.Center;
            drop.style.paddingLeft = 15;
            drop.style.paddingRight = 15;
            drop.style.backgroundColor = Panel;
            drop.style.marginBottom = 8;
            SetBorder(drop, Accent);
            var dropTitle = new Label("拖入衣服或分件菜单");
            dropTitle.style.fontSize = 16;
            drop.Add(dropTitle);
            var hint = new Label("可一次添加多套。请先在分件工具中开启“添加换衣按钮”。");
            hint.style.color = Muted;
            hint.style.marginTop = 8;
            hint.style.whiteSpace = WhiteSpace.Normal;
            drop.Add(hint);
            drop.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                DragAndDrop.visualMode = DragAndDrop.objectReferences.OfType<GameObject>().Any()
                    ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                evt.StopPropagation();
            });
            drop.RegisterCallback<DragPerformEvent>(evt =>
            {
                DragAndDrop.AcceptDrag();
                AddSlots(DragAndDrop.objectReferences.OfType<GameObject>().ToArray());
                evt.StopPropagation();
            });
            root.Add(drop);

            var addSelected = new Button(() => AddSlots(Selection.gameObjects))
            { text = "添加选中衣服" };
            addSelected.style.height = 28;
            addSelected.style.marginBottom = 10;
            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            addSelected.style.flexGrow = 1;
            addSelected.style.flexBasis = 0;
            actions.Add(addSelected);

            var reload = new Button(LoadSelectedGroup)
            { text = "读取已有配置" };
            reload.style.height = 28;
            reload.style.marginBottom = 10;
            reload.style.flexGrow = 1;
            reload.style.flexBasis = 0;
            reload.style.marginLeft = 8;
            actions.Add(reload);
            root.Add(actions);

            var menuNameField = new TextField("菜单名称") { value = collectionMenuName ?? "衣服菜单" };
            collectionMenuNameField = menuNameField;
            menuNameField.style.maxWidth = 500;
            menuNameField.style.marginBottom = 5;
            menuNameField.RegisterValueChangedCallback(evt => collectionMenuName = evt.newValue);
            root.Add(menuNameField);
            var placeholderToggle = new Toggle("使用占位衣服") { value = usePlaceholderOutfit };
            placeholderToggle.tooltip = "占位衣服只作为关闭目标，不占参数值；选择任何正常衣服时都会关闭它。";
            placeholderToggle.style.marginTop = 5;
            placeholderToggle.style.marginBottom = 5;
            placeholderToggle.RegisterValueChangedCallback(evt =>
            {
                usePlaceholderOutfit = evt.newValue;
                BuildUI();
            });
            root.Add(placeholderToggle);
            if (usePlaceholderOutfit)
            {
                var placeholderField = new ObjectField("占位衣服")
                {
                    objectType = typeof(GameObject), allowSceneObjects = true, value = placeholderOutfit
                };
                placeholderField.tooltip = "直接拖入同一角色下的占位衣服，不需要为它制作分件菜单。";
                placeholderField.style.marginBottom = 8;
                placeholderField.RegisterValueChangedCallback(evt =>
                {
                    var selected = evt.newValue as GameObject;
                    placeholderOutfit = TryResolveSlot(selected, out _, out var resolved, out _) ? resolved : selected;
                    placeholderSlot = null;
                    RefreshRows();
                });
                root.Add(placeholderField);
            }

            var advanced = new Foldout { text = "高级设置", value = showAdvanced };
            advanced.RegisterValueChangedCallback(evt => showAdvanced = evt.newValue);
            advanced.style.marginTop = 4;
            advanced.style.marginBottom = 10;
            root.Add(advanced);
            advanced.Add(avatarField);
            parameterField = new TextField("换衣参数") { value = sharedParameter ?? "" };
            parameterField.tooltip = "所有衣服共用一个 Int 参数；无需手动填写，留空会自动生成。";
            parameterField.style.maxWidth = 500;
            parameterField.style.marginBottom = 5;
            parameterField.RegisterValueChangedCallback(evt => sharedParameter = evt.newValue);
            advanced.Add(parameterField);
            var parameterHint = new Label("默认正常衣服为 0，其余依次为 1、2……。占位衣服不占用参数值。");
            parameterHint.style.color = Muted;
            parameterHint.style.whiteSpace = WhiteSpace.Normal;
            parameterHint.style.marginBottom = 10;
            advanced.Add(parameterHint);

            status = new Label();
            status.style.color = Muted;
            status.style.marginBottom = 8;
            status.style.whiteSpace = WhiteSpace.Normal;
            root.Add(status);
            feedback = new HelpBox("", HelpBoxMessageType.Info);
            feedback.style.display = DisplayStyle.None;
            feedback.style.whiteSpace = WhiteSpace.Normal;
            root.Add(feedback);
            generateAllIconsButton = new Button(GenerateAllIcons) { text = "生成全部图标", name = "wardrobe-all-icons" };
            generateAllIconsButton.tooltip = "为已加入的衣服生成整套和分件图标，补齐缺图并更新自动图标。衣服菜单与换衣按钮共用整套图；保留手动图片，覆盖前备份上一版。";
            generateAllIconsButton.style.height = 28;
            generateAllIconsButton.style.marginBottom = 10;
            root.Add(generateAllIconsButton);
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.style.marginBottom = 10;
            slotRows = new VisualElement();
            scroll.Add(slotRows);
            root.Add(scroll);

            generateButton = new Button(Generate) { text = "保存换衣配置" };
            generateButton.name = "wardrobe-primary-action";
            generateButton.style.height = 42;
            generateButton.style.fontSize = 14;
            generateButton.style.backgroundColor = Accent;
            generateButton.style.color = Color.white;
            SetBorder(generateButton, Accent);
            root.Add(generateButton);
            RefreshRows();
            WardrobeUiStyle.Apply(root);
        }

        private static void SetBorder(VisualElement element, Color color)
        {
            element.style.borderTopWidth = 1;
            element.style.borderBottomWidth = 1;
            element.style.borderLeftWidth = 1;
            element.style.borderRightWidth = 1;
            element.style.borderTopColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
            element.style.borderRightColor = color;
            element.style.borderTopLeftRadius = 3;
            element.style.borderTopRightRadius = 3;
            element.style.borderBottomLeftRadius = 3;
            element.style.borderBottomRightRadius = 3;
        }

        private void SetAvatar(VRCAvatarDescriptor candidate)
        {
            if (candidate != null && (EditorUtility.IsPersistent(candidate) || !candidate.gameObject.scene.IsValid()))
            {
                EditorUtility.DisplayDialog("请选择场景角色", "请从层级面板选择场景中的角色。", "确定");
                BuildUI();
                return;
            }
            if (candidate == avatar) return;
            if (switchSlots.Count > 0 && !EditorUtility.DisplayDialog("切换角色",
                    "这会清空窗口里的换衣项列表。继续吗？", "继续", "取消"))
            {
                BuildUI();
                return;
            }
            avatar = candidate;
            switchSlots.Clear();
            defaultSlot = null;
            usePlaceholderOutfit = false;
            placeholderSlot = null;
            placeholderOutfit = null;
            sharedParameter = "";
            collectionMenuName = "衣服菜单";
            loadedCollection = null;
            loadedCollectionName = null;
            TryLoadAvatarCollection(out var message);
            BuildUI();
            if (!string.IsNullOrEmpty(message)) SetFeedback(message);
        }

        private List<WardrobeGeneratedSwitch> AvatarCollections()
        {
            return avatar == null ? new List<WardrobeGeneratedSwitch>() : avatar.transform.Cast<Transform>()
                .Select(child => child.GetComponent<WardrobeGeneratedSwitch>())
                .Where(group => group != null && group.IsSeparateCollection && group.AvatarRoot == avatar.gameObject).ToList();
        }

        private bool TryLoadAvatarCollection(out string message)
        {
            message = null;
            var groups = AvatarCollections();
            if (groups.Count == 0) { loadedCollection = null; loadedCollectionName = null; return false; }
            if (groups.Count > 1)
            {
                loadedCollection = null; loadedCollectionName = null;
                message = "当前角色下有多个衣柜菜单，请先确认要保留的衣柜；不会按名称猜测或重复生成。";
                return false;
            }
            loadedCollection = groups[0];
            loadedCollectionName = loadedCollection.gameObject.name;
            collectionMenuName = loadedCollectionName;
            if (TryLoadGroup(loadedCollection.ParameterName)) return true;
            message = "已找到衣柜「" + loadedCollectionName + "」，但衣服关联不完整；保留已有菜单，保存前会检查。";
            return false;
        }

        private static bool TryResolveSlot(GameObject candidate, out GameObject slot,
            out GameObject outfit, out VRCAvatarDescriptor targetAvatar)
        {
            return TryResolveSlotDetailed(candidate, out slot, out outfit, out targetAvatar, out _);
        }

        private static bool TryResolveSlotDetailed(GameObject candidate, out GameObject slot,
            out GameObject outfit, out VRCAvatarDescriptor targetAvatar, out string reason)
        {
            slot = null;
            outfit = null;
            targetAvatar = null;
            reason = null;
            if (candidate == null || EditorUtility.IsPersistent(candidate) || !candidate.scene.IsValid())
            { reason = "请从层级面板拖入场景物体，不要拖入项目资源"; return false; }
            var marker = candidate.GetComponent<WardrobeGeneratedMenu>()
                         ?? candidate.transform.parent?.GetComponent<WardrobeGeneratedMenu>();
            if (marker == null)
            {
                var owner = candidate.GetComponentInParent<VRCAvatarDescriptor>();
                if (owner == null) { reason = "这个对象不在角色下，或角色缺少 VRC Avatar Descriptor 组件"; return false; }
                // 衣服本体只查对象引用，不能因分件组件被关闭或编辑而认不出衣服。
                var matches = owner.GetComponentsInChildren<WardrobeGeneratedMenu>(true)
                    .Where(entry => entry.ClothingRoot == candidate).ToArray();
                if (matches.Length != 1)
                { reason = matches.Length == 0 ? "没有找到这件衣服的主插件分件菜单" : "这件衣服关联了多个分件菜单"; return false; }
                marker = matches[0];
            }
            if (marker.ClothingRoot == null) { reason = "分件菜单的衣服本体引用丢失"; return false; }
            if (marker.OutfitSwitchNode == null) { reason = "没有换衣按钮，请在分件工具开启“添加换衣按钮”后更新菜单"; return false; }
            outfit = marker.ClothingRoot;
            slot = marker.OutfitSwitchNode;
            targetAvatar = outfit.GetComponentInParent<VRCAvatarDescriptor>();
            if (targetAvatar == null || marker.GetComponentInParent<VRCAvatarDescriptor>() != targetAvatar)
            { reason = "分件菜单与它绑定的衣服不属于同一个角色"; return false; }
            if (slot.transform.parent != marker.transform)
            { reason = "换衣按钮已移出它的分件菜单"; return false; }
            var items = slot.GetComponents<ModularAvatarMenuItem>();
            if (items.Length != 1 || items[0].Control == null ||
                items[0].Control.type != VRCExpressionsMenu.Control.ControlType.Toggle)
            { reason = "换衣按钮需要一个 Toggle 类型的 MA Menu Item"; return false; }
            // 识别不依赖激活状态、默认启用、额外普通组件或其他部件的配置。
            return true;
        }

        private void AddSlots(IEnumerable<GameObject> candidates)
        {
            var messages = new List<string>();
            foreach (var candidate in candidates)
            {
                if (!TryResolveSlotDetailed(candidate, out var slot, out _, out var targetAvatar, out var reason))
                { messages.Add((candidate == null ? "空对象" : candidate.name) + "：" + reason); continue; }
                if (avatar != null && avatar != targetAvatar)
                { messages.Add(candidate.name + "：属于 " + targetAvatar.name + "，当前列表属于 " + avatar.name); continue; }
                if (avatar == null)
                {
                    avatar = targetAvatar;
                    if (!TryLoadAvatarCollection(out var loadMessage) && !string.IsNullOrEmpty(loadMessage)) messages.Add(loadMessage);
                }
                if (switchSlots.Contains(slot)) continue;
                var marker = slot.transform.parent.GetComponent<WardrobeGeneratedMenu>();
                if (switchSlots.Count == 0 && !string.IsNullOrEmpty(marker.OutfitSwitchGroupParameter))
                {
                    // 旧组不完整也不能拒绝当前这个有效的衣服；生成时再安全检查旧配置。
                    if (!TryLoadGroup(marker.OutfitSwitchGroupParameter))
                        messages.Add("已加入当前衣服；旧组未能完整读取，生成前会检查关联");
                    if (switchSlots.Contains(slot)) continue;
                }
                switchSlots.Add(slot);
            }
            if (defaultSlot == null) defaultSlot = switchSlots.FirstOrDefault();
            BuildUI();
            if (messages.Count > 0) SetFeedback(string.Join("\n", messages));
        }

        private void SetFeedback(string message)
        {
            WardrobeDiagnostics.Record("衣柜", "提示", message);
            if (feedback == null) return;
            feedback.text = message;
            feedback.style.display = DisplayStyle.Flex;
        }

        private async void GenerateAllIcons()
        {
            if (iconCaptureBusy || switchSlots.Count == 0) return;
            iconCaptureBusy = true; rootVisualElement.SetEnabled(false);
            try
            {
                var markers = switchSlots.Where(s => s != null && s.transform.parent != null)
                    .Select(s => s.transform.parent.GetComponent<WardrobeGeneratedMenu>()).Where(m => m != null).Distinct().ToArray();
                var result = await WardrobeMenuIconGenerator.GenerateAll(markers, name => { if (this != null) SetFeedback("正在生成图标：" + name); });
                if (this != null) SetFeedback(result.ToString() + "\n图标已写入菜单，请保存场景。");
            }
            catch (Exception e) { if (this != null) SetFeedback("图标处理失败：" + e.GetBaseException().Message); }
            finally { iconCaptureBusy = false; if (this != null) { rootVisualElement.SetEnabled(true); RefreshRows(); } }
        }

        private string DiagnosticState()
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine("窗口配置=" + EditorJsonUtility.ToJson(this, true));
            text.AppendLine("反馈=" + feedback?.text);
            text.AppendLine("同角色衣柜数量=" + (avatar == null ? 0 : AvatarCollections().Count));
            WardrobeDiagnostics.DescribeTree(text, loadedCollection == null ? null : loadedCollection.gameObject);
            foreach (var slot in switchSlots)
                if (slot != null) WardrobeDiagnostics.DescribeTree(text, slot.transform.parent?.gameObject);
            return text.ToString();
        }

        private bool TryLoadGroup(string parameterName)
        {
            if (avatar == null || string.IsNullOrWhiteSpace(parameterName)) return false;
            if (AvatarCollections().Count > 1) return false;
            var found = new List<GameObject>();
            foreach (var marker in avatar.GetComponentsInChildren<WardrobeGeneratedMenu>(true))
            {
                if (marker == null || marker.OutfitSwitchGroupParameter != parameterName) continue;
                if (!TryResolveSlot(marker.gameObject, out var slot, out _, out _)) return false;
                found.Add(slot);
            }
            if (found.Count == 0) return false;
            var collections = AvatarCollections().Where(group => group.ParameterName == parameterName).ToList();
            if (collections.Count > 1) return false;
            var collection = collections.FirstOrDefault();
            found = found.OrderBy(slot => slot.GetComponent<ModularAvatarMenuItem>().Control.value).ToList();
            switchSlots = found;
            sharedParameter = parameterName;
            if (collection != null)
            {
                loadedCollection = collection;
                loadedCollectionName = collection.gameObject.name;
                collectionMenuName = loadedCollectionName;
            }
            usePlaceholderOutfit = collection != null && collection.UsesPlaceholderOutfit;
            placeholderOutfit = usePlaceholderOutfit ? collection.PlaceholderOutfit : null;
            placeholderSlot = found.FirstOrDefault(slot => usePlaceholderOutfit &&
                slot.transform.parent.GetComponent<WardrobeGeneratedMenu>().ClothingRoot == collection.PlaceholderOutfit);
            // 兼容上一版把占位衣服编入参数的记录；新模式不把它加入可选衣服列表。
            if (placeholderSlot != null) found.Remove(placeholderSlot);
            // 默认穿着由组记录与 Int=0 决定，不使用 Menu Item 的“默认启用”。
            defaultSlot = found.FirstOrDefault(slot => collection != null &&
                              slot.transform.parent.GetComponent<WardrobeGeneratedMenu>().ClothingRoot ==
                              collection.DefaultOutfit)
                          ?? found.FirstOrDefault(slot => Mathf.Approximately(
                              slot.GetComponent<ModularAvatarMenuItem>().Control.value, 0))
                          ?? found.FirstOrDefault();
            return true;
        }

        private void LoadSelectedGroup()
        {
            var candidate = Selection.activeGameObject;
            var selectedAvatar = candidate?.GetComponentInParent<VRCAvatarDescriptor>();
            if (selectedAvatar != null && selectedAvatar != avatar) { SetAvatar(selectedAvatar); return; }
            bool loaded = TryLoadAvatarCollection(out var message);
            BuildUI();
            SetFeedback(loaded ? "已读取衣柜「" + loadedCollectionName + "」。" : message ?? "当前角色下没有找到本工具生成的衣柜菜单。");
        }

        private void RefreshRows()
        {
            if (loadedCollection != null && loadedCollection.AvatarRoot == avatar?.gameObject) WardrobeMenuIconGenerator.SyncCollectionIcon(loadedCollection);
            if (slotRows == null) return;
            switchSlots.RemoveAll(slot => !TryResolveSlot(slot, out _, out _, out var owner) ||
                                          avatar != null && owner != avatar);
            if (usePlaceholderOutfit && placeholderOutfit != null)
                switchSlots.RemoveAll(slot => slot.transform.parent.GetComponent<WardrobeGeneratedMenu>().ClothingRoot == placeholderOutfit);
            if (!switchSlots.Contains(defaultSlot)) defaultSlot = switchSlots.FirstOrDefault();
            status.text = "已添加 " + switchSlots.Count + " 套衣服 · 默认穿着：" +
                          (defaultSlot == null ? "未选择" :
                              defaultSlot.transform.parent.GetComponent<WardrobeGeneratedMenu>().ClothingRoot.name);
            if (usePlaceholderOutfit)
                status.text += "\n占位衣服：" + (placeholderOutfit == null ? "请拖入上方输入框" : placeholderOutfit.name + "（仅关闭）");
            if (loadedCollection != null && loadedCollection.AvatarRoot == avatar?.gameObject)
                status.text += "\n当前衣柜：" + loadedCollection.gameObject.name;
            if (AvatarCollections().Count > 1) status.text += "\n当前角色下有多个衣柜，无法自动锁定，请先确认场景中的菜单。";
            generateButton.SetEnabled(avatar != null && switchSlots.Count >= (usePlaceholderOutfit ? 1 : 2) &&
                (!usePlaceholderOutfit || placeholderOutfit != null) && AvatarCollections().Count <= 1);
            generateAllIconsButton?.SetEnabled(avatar != null && switchSlots.Count > 0 && !iconCaptureBusy);
            slotRows.Clear();
            foreach (var slot in switchSlots.ToArray())
            {
                if (!TryResolveSlot(slot, out _, out var outfit, out _)) continue;
                var captured = slot;
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.minHeight = 56;
                row.style.marginBottom = 8;
                row.style.paddingLeft = 9;
                row.style.paddingRight = 7;
                row.style.backgroundColor = Panel;
                SetBorder(row, slot == defaultSlot ? Accent : Border);
                var label = new Label(outfit.name);
                label.style.fontSize = 14;
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.minWidth = 0;
                label.style.marginRight = 10;
                label.tooltip = "分件菜单：" + slot.transform.parent.name + "\n换衣按钮：" + slot.name;
                label.style.flexGrow = 1;
                row.Add(label);
                var choose = new Button(() =>
                {
                    defaultSlot = captured;
                    RefreshRows();
                }) { text = slot == defaultSlot ? "默认穿着" : "设为默认" };
                choose.style.height = 26;
                choose.style.minWidth = 78;
                choose.tooltip = "选择进入游戏时默认穿着的衣服，不影响场景里的衣服显示。";
                choose.style.borderTopLeftRadius = 6;
                choose.style.borderTopRightRadius = 6;
                choose.style.borderBottomLeftRadius = 6;
                choose.style.borderBottomRightRadius = 6;
                row.Add(choose);
                var remove = new Button(() =>
                {
                    switchSlots.Remove(captured);
                    RefreshRows();
                }) { text = "移除" };
                remove.name = "wardrobe-remove-outfit";
                remove.tooltip = "仅从待配置列表中移除，不删除衣服或分件菜单。";
                remove.style.minWidth = 52;
                remove.style.height = 26;
                remove.style.borderTopLeftRadius = 6;
                remove.style.borderTopRightRadius = 6;
                remove.style.borderBottomLeftRadius = 6;
                remove.style.borderBottomRightRadius = 6;
                remove.style.marginLeft = 6;
                row.Add(remove);
                slotRows.Add(row);
            }
            WardrobeUiStyle.Apply(rootVisualElement);
        }

        private static Component GetInstallTarget(GameObject node)
        {
            return node == null || InstallTargetType == null ? null : node.GetComponent(InstallTargetType);
        }

        private static ModularAvatarMenuInstaller ReferencedInstaller(GameObject node)
        {
            var component = GetInstallTarget(node);
            if (component == null) return null;
            return new SerializedObject(component).FindProperty("installer")?.objectReferenceValue
                as ModularAvatarMenuInstaller;
        }

        internal static bool IsInstallTargetFor(GameObject node, ModularAvatarMenuInstaller installer)
        {
            return installer != null && ReferencedInstaller(node) == installer;
        }

        internal static bool TryRetargetInstallTarget(GameObject node, ModularAvatarMenuInstaller installer)
        {
            var component = GetInstallTarget(node);
            if (component == null || installer == null) return false;
            var serialized = new SerializedObject(component);
            var property = serialized.FindProperty("installer");
            if (property == null) return false;
            Undo.RecordObject(component, "更新衣服菜单引用");
            property.objectReferenceValue = installer;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(component);
            return true;
        }

        private static bool ValidateCollection(WardrobeGeneratedSwitch group, List<GameObject> members)
        {
            if (group == null || group.Entries == null || group.Entries.Count != members.Count ||
                group.GetComponents<WardrobeGeneratedSwitch>().Length != 1 ||
                group.GetComponents<ModularAvatarMenuItem>().Length != 1 ||
                group.GetComponents<ModularAvatarMenuInstaller>().Length != 1 ||
                group.GetComponents<ModularAvatarParameters>().Length != 1 ||
                group.GetComponents<Component>().Any(component => component == null ||
                    (!(component is Transform) && !(component is WardrobeGeneratedSwitch) &&
                     !(component is ModularAvatarMenuItem) && !(component is ModularAvatarMenuInstaller) &&
                     !(component is ModularAvatarParameters))))
                return false;
            var menu = group.GetComponent<ModularAvatarMenuItem>();
            var parameters = group.GetComponent<ModularAvatarParameters>();
            if (menu.Control == null || menu.Control.type != VRCExpressionsMenu.Control.ControlType.SubMenu ||
                menu.MenuSource != SubmenuSource.Children || menu.menuSource_otherObjectChildren != null ||
                parameters.parameters == null || parameters.parameters.Count != 1 ||
                parameters.parameters[0].nameOrPrefix != group.ParameterName ||
                parameters.parameters[0].syncType != ParameterSyncType.Int)
                return false;
            var outfitRoots = new HashSet<GameObject>(group.Entries.Select(entry => entry?.OutfitRoot));
            if (outfitRoots.Contains(null) || outfitRoots.Count != members.Count) return false;
            if (group.DirectOutfitToggles && group.UsesPlaceholderOutfit)
            {
                if (group.PlaceholderOutfit == null) return false;
                outfitRoots.Add(group.PlaceholderOutfit);
            }
            if (group.UsesMergeMotion &&
                (group.DirectOutfitToggles || !(group.GeneratedMotion is BlendTree) ||
                 group.GeneratedClips == null || group.GeneratedClips.Count != members.Count ||
                 group.GeneratedClips.Any(clip => clip == null))) return false;
            var seen = new HashSet<GameObject>();
            var childNodes = new HashSet<GameObject>();
            foreach (var entry in group.Entries)
            {
                if (entry == null || entry.OutfitRoot == null ||
                    entry.PartMenuRoot == null || entry.WearNode == null ||
                    !members.Contains(entry.WearNode) || !seen.Add(entry.WearNode) ||
                    entry.WearNode.transform.parent != entry.PartMenuRoot.transform)
                    return false;
                if (group.UsesMergeMotion)
                {
                    if (entry.MenuNode != null ||
                        entry.WearNode.GetComponent<ModularAvatarObjectToggle>() != null) return false;
                    var mergedMotion = entry.WearNode.GetComponent<ModularAvatarMergeBlendTree>();
                    if (entry.OutfitRoot == group.DefaultOutfit)
                    {
                        if (mergedMotion == null || !mergedMotion.enabled ||
                            mergedMotion.Motion != group.GeneratedMotion ||
                            mergedMotion.PathMode != MergeAnimatorPathMode.Absolute) return false;
                    }
                    else if (mergedMotion != null) return false;
                }
                else if (group.DirectOutfitToggles)
                {
                    if (entry.MenuNode != null) return false;
                    var toggle = entry.WearNode.GetComponent<ModularAvatarObjectToggle>();
                    if (toggle == null || !toggle.enabled || toggle.Inverted ||
                        entry.WearNode.GetComponents<ModularAvatarObjectToggle>().Length != 1 ||
                        toggle.Objects == null || toggle.Objects.Count != outfitRoots.Count) return false;
                    var seenRoots = new HashSet<GameObject>();
                    foreach (var rule in toggle.Objects)
                    {
                        var target = rule.Object?.Get(toggle);
                        if (target == null || !outfitRoots.Contains(target) || !seenRoots.Add(target) ||
                            rule.Active != (target == entry.OutfitRoot)) return false;
                    }
                }
                else
                {
                    if (entry.MenuNode == null || entry.MenuNode.transform.parent != group.transform ||
                        entry.MenuNode.transform.childCount != 0 ||
                        entry.MenuNode.GetComponents<ModularAvatarObjectToggle>().Length != 1 ||
                        entry.MenuNode.GetComponent<ModularAvatarMenuItem>() != null ||
                        entry.MenuNode.GetComponents<Component>().Any(component => component == null ||
                            (!(component is Transform) && !(component is ModularAvatarObjectToggle))) ||
                        !childNodes.Add(entry.MenuNode)) return false;
                }
                if (group.UsesInstallTargets)
                {
                    if (entry.PartsNode == null || entry.PartsNode.transform.parent != group.transform ||
                        entry.PartsNode.transform.childCount != 0 || !entry.PartsNode.activeSelf ||
                        !childNodes.Add(entry.PartsNode) ||
                        entry.PartsNode.GetComponents(InstallTargetType).Length != 1 ||
                        entry.PartMenuRoot.transform.parent != group.transform.parent ||
                        entry.PartMenuRoot.GetComponent<ModularAvatarMenuInstaller>() == null ||
                        ReferencedInstaller(entry.PartsNode) !=
                        entry.PartMenuRoot.GetComponent<ModularAvatarMenuInstaller>() ||
                        entry.PartsNode.GetComponents<Component>().Any(component => component == null ||
                            (!(component is Transform) && component.GetType() != InstallTargetType)))
                        return false;
                }
                else if (entry.PartMenuRoot.transform.parent != group.transform ||
                         entry.PartMenuRoot.GetComponent<ModularAvatarMenuInstaller>() != null ||
                         !childNodes.Add(entry.PartMenuRoot))
                {
                    return false;
                }
            }
            return childNodes.Count == members.Count * (group.DirectOutfitToggles || group.UsesMergeMotion ? 1 : 2);
        }

        // 旧版在重建分件菜单的瞬间，MA Inspector 可能保留一个已销毁的菜单安装器引用。
        // 这不是用户手动改过菜单的信号；只要本工具的关联记录仍完整，就允许本次生成重建引用。
        private static bool CanRepairGeneratedCollection(WardrobeGeneratedSwitch group,
            List<GameObject> members, VRCAvatarDescriptor avatar)
        {
            if (group == null || avatar == null || group.AvatarRoot != avatar.gameObject ||
                !group.IsSeparateCollection || group.Entries == null ||
                group.Entries.Count != members.Count ||
                group.GetComponent<ModularAvatarMenuItem>() == null ||
                group.GetComponent<ModularAvatarMenuInstaller>() == null ||
                group.GetComponent<ModularAvatarParameters>() == null) return false;

            var parameterConfigs = group.GetComponent<ModularAvatarParameters>().parameters;
            if (parameterConfigs == null || parameterConfigs.Count != 1 ||
                parameterConfigs[0].nameOrPrefix != group.ParameterName || parameterConfigs[0].syncType != ParameterSyncType.Int)
                return false;

            var seenWearNodes = new HashSet<GameObject>();
            var seenPartMenus = new HashSet<GameObject>();
            var generatedChildren = new HashSet<GameObject>();
            foreach (var entry in group.Entries)
            {
                if (entry == null || entry.OutfitRoot == null || entry.WearNode == null ||
                    entry.PartMenuRoot == null || !members.Contains(entry.WearNode) ||
                    !seenWearNodes.Add(entry.WearNode) || !seenPartMenus.Add(entry.PartMenuRoot) ||
                    entry.WearNode.transform.parent != entry.PartMenuRoot.transform ||
                    (entry.MenuNode != null && entry.MenuNode.transform.parent != group.transform) ||
                    (entry.PartsNode != null && entry.PartsNode.transform.parent != group.transform))
                    return false;

                var partMarker = entry.PartMenuRoot.GetComponent<WardrobeGeneratedMenu>();
                if (partMarker == null || partMarker.ClothingRoot != entry.OutfitRoot ||
                    partMarker.OutfitSwitchNode != entry.WearNode)
                    return false;
                foreach (var node in new[] { entry.MenuNode, entry.PartsNode,
                             entry.PartMenuRoot.transform.parent == group.transform ? entry.PartMenuRoot : null }
                         .Where(node => node != null).Distinct())
                    if (!generatedChildren.Add(node)) return false;
                if (entry.PartsNode != null && group.UsesInstallTargets && entry.PartsNode.GetComponents(InstallTargetType).Length > 1)
                    return false;
            }
            // 额外子节点不属于本工具，修复或更新时原样保留。
            return true;
        }

        private void Generate()
        {
            WardrobeDiagnostics.Record("衣柜", "保存换衣配置", "开始");
            var collections = AvatarCollections();
            if (collections.Count > 1)
            {
                SetFeedback("当前角色下有多个衣柜菜单，无法自动锁定。未修改，也不会再生成一个。");
                return;
            }
            var collection = collections.FirstOrDefault();
            if (collection != null)
            {
                if (string.IsNullOrWhiteSpace(collectionMenuName) || collectionMenuName == loadedCollectionName ||
                    loadedCollection == null && collectionMenuName == "衣服菜单")
                    collectionMenuName = collection.gameObject.name;
                loadedCollection = collection;
            }
            switchSlots.RemoveAll(slot => slot == null);
            if (avatar == null || EditorUtility.IsPersistent(avatar) || !avatar.gameObject.scene.IsValid() ||
                switchSlots.Count < (usePlaceholderOutfit ? 1 : 2) || switchSlots.Count > 255 || !switchSlots.Contains(defaultSlot) ||
                switchSlots.Distinct().Count() != switchSlots.Count ||
                usePlaceholderOutfit && (placeholderOutfit == null || EditorUtility.IsPersistent(placeholderOutfit) ||
                    placeholderOutfit.GetComponentInParent<VRCAvatarDescriptor>() != avatar))
            {
                EditorUtility.DisplayDialog("选择不完整", usePlaceholderOutfit
                    ? "请拖入同一角色下的占位衣服，并添加正常衣服作为默认穿着。"
                    : "请添加同一个角色的至少两套衣服，并选择默认穿着。", "确定");
                return;
            }
            collectionMenuName = (collectionMenuName ?? "").Trim();
            if (string.IsNullOrEmpty(collectionMenuName))
            {
                EditorUtility.DisplayDialog("总菜单名称为空", "请填写衣服总菜单的名称。", "确定");
                return;
            }
            if (InstallTargetType == null)
            {
                EditorUtility.DisplayDialog("MA 版本不支持", "当前 Modular Avatar 未提供 MA Menu Install Target，无法在不移动原菜单的情况下生成总菜单入口。", "确定");
                return;
            }
            if (avatar.transform.Cast<Transform>().Any(child =>
                    child.GetComponent<WardrobeGeneratedSwitch>() is WardrobeGeneratedSwitch oldMenu &&
                    oldMenu.AvatarRoot == avatar.gameObject && !oldMenu.IsSeparateCollection))
            {
                EditorUtility.DisplayDialog("发现旧版合并菜单",
                    "Avatar 下还有上一版生成的整套衣柜菜单。工具不会自动覆盖；请先检查并停用旧切换逻辑。", "确定");
                return;
            }
            var outfitBySlot = new Dictionary<GameObject, GameObject>();
            foreach (var slot in switchSlots)
            {
                if (!TryResolveSlotDetailed(slot, out _, out var outfit, out var targetAvatar, out var reason) || targetAvatar != avatar)
                {
                    SetFeedback((slot == null ? "空对象" : slot.name) + "：" +
                        (reason ?? "这个换衣按钮属于另一个角色，未修改。"));
                    return;
                }
                if (slot.GetComponents<ModularAvatarObjectToggle>().Length > 1 ||
                    slot.GetComponents<ModularAvatarMergeBlendTree>().Length > 1)
                {
                    SetFeedback(slot.name + "：有重复 MA 控制组件，未自动覆盖。");
                    return;
                }
                outfitBySlot[slot] = outfit;
            }
            if (outfitBySlot.Values.Distinct().Count() != switchSlots.Count)
            {
                EditorUtility.DisplayDialog("重复衣服", "同一套衣服只能加入一个换衣项。", "确定");
                return;
            }
            if (outfitBySlot.Values.Select(root =>
                    AnimationUtility.CalculateTransformPath(root.transform, avatar.transform))
                .Distinct(StringComparer.Ordinal).Count() != switchSlots.Count)
            {
                EditorUtility.DisplayDialog("衣服路径重复",
                    "有两套衣服在 Avatar 下使用相同的层级名称。动画无法区分它们，请先改成不同名称。", "确定");
                return;
            }

            var oldGroups = switchSlots.Select(slot => slot.transform.parent.GetComponent<WardrobeGeneratedMenu>()
                    .OutfitSwitchGroupParameter)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToList();
            if (oldGroups.Count > 1)
            {
                EditorUtility.DisplayDialog("分属不同换衣组", "所选项已属于不同换衣组。请分组分别处理，避免覆盖其他衣服。", "确定");
                return;
            }
            string parameterName = (sharedParameter ?? "").Trim();
            if (string.IsNullOrEmpty(parameterName))
                parameterName = collection != null ? collection.ParameterName : oldGroups.Count == 1 ? oldGroups[0] : NewParameterName();
            if (collection != null && oldGroups.Count == 1 && oldGroups[0] != collection.ParameterName)
            {
                SetFeedback("所选换衣项的关联参数与当前角色的衣柜不一致，未覆盖。");
                return;
            }

            var oldMembers = new List<GameObject>();
            var previousParameter = collection != null ? collection.ParameterName : oldGroups.FirstOrDefault();
            if (!string.IsNullOrEmpty(previousParameter))
            {
                foreach (var marker in avatar.GetComponentsInChildren<WardrobeGeneratedMenu>(true))
                {
                    if (marker == null || marker.OutfitSwitchGroupParameter != previousParameter) continue;
                    if (!TryResolveSlot(marker.gameObject, out var slot, out _, out _))
                    {
                        EditorUtility.DisplayDialog("旧换衣组不完整", "旧菜单有损坏的换衣项，未覆盖。", "确定");
                        return;
                    }
                    oldMembers.Add(slot);
                }
            }
            else if (avatar.GetComponentsInChildren<WardrobeGeneratedMenu>(true).Any(marker =>
                         marker.OutfitSwitchGroupParameter == parameterName))
            {
                EditorUtility.DisplayDialog("参数已属于其他换衣组", "请先选中那个组的换衣项并读取它，再加入新衣服。", "确定");
                return;
            }
            bool repairingCollection = collection != null &&
                                       !ValidateCollection(collection, oldMembers);
            if (repairingCollection && !CanRepairGeneratedCollection(collection, oldMembers, avatar))
            {
                EditorUtility.DisplayDialog("总菜单结构已改变", "已有总菜单的整理节点或分件菜单不完整，未覆盖。", "确定");
                return;
            }
            if (avatar.transform.Cast<Transform>().Any(child =>
                    child.name == collectionMenuName && (collection == null || child.gameObject != collection.gameObject)))
            {
                EditorUtility.DisplayDialog("总菜单名称被占用", "角色下已有同名物体，请更换总菜单名称。", "确定");
                return;
            }
            var partMenus = new Dictionary<GameObject, GameObject>();
            foreach (var pair in outfitBySlot)
            {
                var menu = pair.Key.transform.parent.gameObject;
                var marker = menu.GetComponent<WardrobeGeneratedMenu>();
                var submenu = menu.GetComponent<ModularAvatarMenuItem>();
                if (marker == null || marker.ClothingRoot != pair.Value || marker.OutfitSwitchNode != pair.Key ||
                    submenu == null || submenu.Control == null ||
                    submenu.Control.type != VRCExpressionsMenu.Control.ControlType.SubMenu)
                {
                    SetFeedback(pair.Value.name + "：分件菜单或换衣按钮的关联已损坏，未覆盖。");
                    return;
                }
                var parent = menu.transform.parent;
                if (parent != avatar.transform && (collection == null || parent != collection.transform))
                {
                    EditorUtility.DisplayDialog("分件菜单属于其他总菜单", pair.Value.name + " 已属于另一个集合。", "确定");
                    return;
                }
                if (parent == avatar.transform)
                {
                    var installer = menu.GetComponent<ModularAvatarMenuInstaller>();
                    if (installer == null)
                    {
                        EditorUtility.DisplayDialog("分件菜单缺少安装组件",
                            pair.Value.name + " 的原分件菜单没有 MA Menu Installer，无法建立不移动菜单的引用。", "确定");
                        return;
                    }
                }
                partMenus[pair.Key] = menu;
            }
            var allowedSlots = new HashSet<GameObject>(switchSlots.Concat(oldMembers));
            if (avatar.GetComponentsInChildren<ModularAvatarMenuItem>(true).Any(item =>
                    !allowedSlots.Contains(item.gameObject) && item.Control?.parameter?.name == parameterName))
            {
                EditorUtility.DisplayDialog("参数名称冲突", "这个参数已被其他 MA 菜单项使用，请换一个名称。", "确定");
                return;
            }
            var declared = avatar.expressionParameters?.parameters?.FirstOrDefault(parameter =>
                parameter != null && parameter.name == parameterName);
            if (declared != null && declared.valueType != VRCExpressionParameters.ValueType.Int)
            {
                EditorUtility.DisplayDialog("参数类型冲突", "角色的这个参数不是 Int 类型，请更换名称或调整原有参数。", "确定");
                return;
            }
            int defaultParameterValue = 0;
            if (declared != null && !Mathf.Approximately(declared.defaultValue, defaultParameterValue))
            {
                EditorUtility.DisplayDialog("参数默认值冲突", "角色原有的换衣参数默认值不是 " + defaultParameterValue +
                    "；请更换参数名称，避免默认穿着不一致。", "确定");
                return;
            }
            if (avatar.GetComponentsInChildren<ModularAvatarParameters>(true).Any(component =>
                    component != (collection == null ? null : collection.GetComponent<ModularAvatarParameters>()) &&
                    component.parameters != null && component.parameters.Any(parameter =>
                        !parameter.isPrefix && parameter.nameOrPrefix == parameterName)))
            {
                EditorUtility.DisplayDialog("参数已由其他 MA 组件声明", "请换一个参数名称，避免与其他 MA Parameters 重复。", "确定");
                return;
            }
            var selectedOutfits = new HashSet<GameObject>(outfitBySlot.Values);
            if (usePlaceholderOutfit && placeholderOutfit != null) selectedOutfits.Add(placeholderOutfit);
            var outfitPaths = outfitBySlot.Values.Select(outfit =>
                AnimationUtility.CalculateTransformPath(outfit.transform, avatar.transform)).ToList();
            if (outfitPaths.Distinct().Count() != outfitPaths.Count || outfitBySlot.Values.Any(outfit =>
                    outfit.name.Contains("/") || avatar.transform.Cast<Transform>().Count(child =>
                        child.name == outfit.name) != 1))
            {
                EditorUtility.DisplayDialog("衣服名称无法区分",
                    "同一个角色下，衣服名称不能重复或包含 /。请先修改衣服名称，再保存配置。", "确定");
                return;
            }
            var allowedToggleOwners = new HashSet<GameObject>(allowedSlots);
            if (collection != null && collection.Entries != null)
                foreach (var entry in collection.Entries.Where(entry => entry?.MenuNode != null))
                    allowedToggleOwners.Add(entry.MenuNode);
            var conflicting = avatar.GetComponentsInChildren<ModularAvatarObjectToggle>(true)
                .FirstOrDefault(toggle => !allowedToggleOwners.Contains(toggle.gameObject) &&
                    toggle.Objects != null && toggle.Objects.Any(entry =>
                        entry.Object != null && selectedOutfits.Contains(entry.Object.Get(toggle))));
            if (conflicting != null)
            {
                EditorUtility.DisplayDialog("衣服根对象已有其他开关",
                    "「" + conflicting.gameObject.name + "」也控制了所选衣服的根物体。请先处理旧切换逻辑，避免互相覆盖。", "确定");
                return;
            }

            var ordered = new List<GameObject> { defaultSlot };
            ordered.AddRange(switchSlots.Where(slot => !ordered.Contains(slot)));
            var roots = ordered.Select(slot => outfitBySlot[slot]).ToList();
            if (usePlaceholderOutfit && roots.Any(root => root == placeholderOutfit ||
                    root.transform.IsChildOf(placeholderOutfit.transform) || placeholderOutfit.transform.IsChildOf(root.transform)))
            {
                SetFeedback("占位衣服不能与正常衣服重叠或互为父子物体，未修改。");
                return;
            }
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("注入 MA 换衣开关及衣服总菜单");
            try
            {
                var previousLinks = collection == null
                    ? new Dictionary<GameObject, WardrobeSwitchLink>()
                    : collection.Entries.ToDictionary(entry => entry.WearNode);
                var previousOrder = collection == null ? Array.Empty<GameObject>() :
                    collection.transform.Cast<Transform>().Select(child => child.gameObject).ToArray();
                var generatedChildren = new HashSet<GameObject>(previousLinks.Values.SelectMany(entry =>
                    new[] { entry.MenuNode, entry.PartsNode,
                        entry.PartMenuRoot != null && entry.PartMenuRoot.transform.parent == collection.transform ? entry.PartMenuRoot : null })
                    .Where(node => node != null));
                bool migratingMovedMenus = collection != null && !collection.UsesInstallTargets;
                var removedNodes = previousLinks.Values.SelectMany(entry => new[] { entry.MenuNode,
                        !migratingMovedMenus && !switchSlots.Contains(entry.WearNode) ? entry.PartsNode : null })
                    .Where(node => node != null).ToArray();
                if (collection != null) CheckExtraReferences(previousOrder.Where(node => !generatedChildren.Contains(node)), removedNodes);
                if (collection == null)
                {
                    var collectionObject = CreateChild(avatar.transform, collectionMenuName);
                    Undo.RecordObject(collectionObject.transform, "排列衣服总菜单");
                    collectionObject.transform.SetSiblingIndex(roots.Min(root => root.transform.GetSiblingIndex()));
                    collection = Undo.AddComponent<WardrobeGeneratedSwitch>(collectionObject);
                    var topMenu = Undo.AddComponent<ModularAvatarMenuItem>(collectionObject);
                    topMenu.MenuSource = SubmenuSource.Children;
                    topMenu.Control = new VRCExpressionsMenu.Control
                    {
                        name = collectionMenuName,
                        type = VRCExpressionsMenu.Control.ControlType.SubMenu
                    };
                    topMenu.label = "";
                    Undo.AddComponent<ModularAvatarMenuInstaller>(collectionObject);
                    Undo.AddComponent<ModularAvatarParameters>(collectionObject);
                    EditorUtility.SetDirty(topMenu);
                }
                else
                {
                    Undo.RecordObject(collection.gameObject, "更新衣服总菜单名称");
                    Undo.RecordObject(collection.GetComponent<ModularAvatarMenuItem>(), "同步总菜单名称");
                    collection.gameObject.name = collectionMenuName;
                    collection.GetComponent<ModularAvatarMenuItem>().label = "";
                }
                Undo.RegisterCompleteObjectUndo(collection, "记录衣服总菜单关联");
                collection.AvatarRoot = avatar.gameObject;
                collection.IsSeparateCollection = true;
                collection.UsesInstallTargets = true;
                collection.DirectOutfitToggles = true;
                collection.UsesMergeMotion = false;
                // 解除旧动画引用；旧资产保留，避免影响用户自行引用它们的其他功能。
                collection.GeneratedMotion = null;
                collection.GeneratedClips = new List<AnimationClip>();
                collection.ParameterName = parameterName;
                collection.DefaultOutfit = outfitBySlot[defaultSlot];
                collection.UsesPlaceholderOutfit = usePlaceholderOutfit;
                collection.PlaceholderOutfit = usePlaceholderOutfit ? placeholderOutfit : null;
                var parameterComponent = collection.GetComponent<ModularAvatarParameters>();
                Undo.RecordObject(parameterComponent, "声明 Int 换衣参数");
                parameterComponent.parameters = new List<ParameterConfig>
                {
                    new ParameterConfig
                    {
                        nameOrPrefix = parameterName,
                        syncType = ParameterSyncType.Int,
                        saved = true,
                        defaultValue = defaultParameterValue,
                        hasExplicitDefaultValue = true
                    }
                };
                EditorUtility.SetDirty(parameterComponent);

                if (migratingMovedMenus)
                {
                    // 将上一版副工具移动过的菜单先恢复到 Avatar 原层级。
                    foreach (var oldLink in previousLinks.Values)
                    {
                        var oldMenu = oldLink.PartMenuRoot;
                        Undo.SetTransformParent(oldMenu.transform, avatar.transform, "恢复原分件菜单层级");
                        Undo.RecordObject(oldMenu.transform, "恢复分件菜单顺序");
                        oldMenu.transform.SetSiblingIndex(oldLink.OutfitRoot.transform.GetSiblingIndex());
                        var restoredInstaller = Undo.AddComponent<ModularAvatarMenuInstaller>(oldMenu);
                        restoredInstaller.installTargetMenu = oldLink.PartInstallTargetMenu;
                        restoredInstaller.menuToAppend = oldLink.PartMenuToAppend;
                        restoredInstaller.enabled = oldLink.PartInstallerWasEnabled;
                        EditorUtility.SetDirty(restoredInstaller);
                    }
                }

                foreach (var removed in oldMembers.Where(slot => !switchSlots.Contains(slot)))
                {
                    var item = removed.GetComponent<ModularAvatarMenuItem>();
                    var marker = removed.transform.parent.GetComponent<WardrobeGeneratedMenu>();
                    Undo.RecordObject(item, "清空移出衣服的换衣参数");
                    Undo.RecordObject(marker, "清空移出衣服的互斥关联");
                    item.Control.parameter = new VRCExpressionsMenu.Control.Parameter { name = "" };
                    item.Control.value = 2;
                    item.automaticValue = false;
                    item.isDefault = false;
                    marker.OutfitSwitchGroupParameter = null;
                    var oldToggle = removed.GetComponent<ModularAvatarObjectToggle>();
                    if (oldToggle != null) Undo.DestroyObjectImmediate(oldToggle);
                    var oldMotion = removed.GetComponent<ModularAvatarMergeBlendTree>();
                    if (oldMotion != null) Undo.DestroyObjectImmediate(oldMotion);
                    EditorUtility.SetDirty(item);
                    EditorUtility.SetDirty(marker);
                }

                foreach (var oldLink in previousLinks.Values)
                {
                    if (oldLink.MenuNode != null) Undo.DestroyObjectImmediate(oldLink.MenuNode);
                    if (oldLink.PartsNode != null && !migratingMovedMenus && !switchSlots.Contains(oldLink.WearNode))
                        Undo.DestroyObjectImmediate(oldLink.PartsNode);
                }

                collection.Entries = new List<WardrobeSwitchLink>();
                var menuReferences = new List<GameObject>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var slot = ordered[i];
                    var item = slot.GetComponent<ModularAvatarMenuItem>();
                    var marker = slot.transform.parent.GetComponent<WardrobeGeneratedMenu>();
                    var partMenu = partMenus[slot];
                    var partInstaller = partMenu.GetComponent<ModularAvatarMenuInstaller>();
                    if (partInstaller == null || partMenu.transform.parent != avatar.transform)
                        throw new InvalidOperationException("原分件菜单未成功恢复到 Avatar 一级层级。");

                    var menuReference = !migratingMovedMenus && previousLinks.TryGetValue(slot, out var previousLink)
                        ? previousLink.PartsNode : null;
                    if (menuReference == null) menuReference = CreateChild(collection.transform, "菜单引用_" + partMenu.name);
                    // MA 仍会读取此节点；隐藏它，让 Hierarchy 只保留干净的「衣服菜单」外壳。
                    Undo.RecordObject(menuReference, "保留衣服菜单引用节点");
                    menuReference.hideFlags = HideFlags.HideInHierarchy;
                    menuReference.SetActive(true);
                    var installTarget = GetInstallTarget(menuReference) ?? Undo.AddComponent(menuReference, InstallTargetType);
                    if (installTarget is Behaviour behaviour)
                    { Undo.RecordObject(behaviour, "启用衣服菜单引用"); behaviour.enabled = true; }
                    if (installTarget == null || !TryRetargetInstallTarget(menuReference, partInstaller))
                        throw new InvalidOperationException("无法创建 MA Menu Install Target 引用。");
                    menuReferences.Add(menuReference);

                    Undo.RecordObject(item, "配置 Int 换衣参数");
                    Undo.RecordObject(marker, "记录换衣换衣组");
                    item.Control.parameter = new VRCExpressionsMenu.Control.Parameter { name = parameterName };
                    item.enabled = true;
                    if (!slot.activeSelf)
                    {
                        Undo.RecordObject(slot, "启用换衣菜单项");
                        slot.SetActive(true);
                    }
                    item.Control.value = i;
                    item.automaticValue = false;
                    item.isDefault = false;
                    item.isSaved = true;
                    item.isSynced = true;
                    marker.OutfitSwitchGroupParameter = parameterName;
                    var toggle = slot.GetComponent<ModularAvatarObjectToggle>();
                    if (toggle == null) toggle = Undo.AddComponent<ModularAvatarObjectToggle>(slot);
                    Undo.RecordObject(toggle, "注入整套衣服互斥开关");
                    toggle.enabled = true;
                    toggle.Inverted = false;
                    toggle.Objects = roots.Select((outfit, index) => new ToggledObject
                    {
                        Object = new AvatarObjectReference(outfit),
                        Active = index == i
                    }).ToList();
                    if (usePlaceholderOutfit)
                        toggle.Objects.Add(new ToggledObject
                        {
                            Object = new AvatarObjectReference(placeholderOutfit), Active = false
                        });
                    EditorUtility.SetDirty(toggle);
                    var oldMotion = slot.GetComponent<ModularAvatarMergeBlendTree>();
                    if (oldMotion != null) Undo.DestroyObjectImmediate(oldMotion);
                    collection.Entries.Add(new WardrobeSwitchLink
                    {
                        OutfitRoot = roots[i],
                        MenuNode = null,
                        WearNode = slot,
                        PartsNode = menuReference,
                        PartMenuRoot = partMenu,
                        PartInstallerWasEnabled = partInstaller.enabled,
                        PartInstallTargetMenu = partInstaller.installTargetMenu,
                        PartMenuToAppend = partInstaller.menuToAppend
                    });
                    EditorUtility.SetDirty(item);
                    EditorUtility.SetDirty(marker);
                }
                var referenceQueue = new Queue<GameObject>(menuReferences);
                var order = new List<GameObject>();
                foreach (var node in previousOrder)
                    if (!generatedChildren.Contains(node)) { if (node != null) order.Add(node); }
                    else if (referenceQueue.Count > 0) order.Add(referenceQueue.Dequeue());
                order.AddRange(referenceQueue);
                foreach (var node in order) Undo.RecordObject(node.transform, "保留衣柜菜单顺序");
                for (int i = 0; i < order.Count; i++) order[i].transform.SetSiblingIndex(i);
                EditorUtility.SetDirty(collection);
                AssetDatabase.SaveAssets();
                WardrobeMenuIconGenerator.SyncCollectionIcon(collection);
                EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                Undo.CollapseUndoOperations(undoGroup);
                switchSlots = ordered;
                defaultSlot = ordered[defaultParameterValue];
                sharedParameter = parameterName;
                loadedCollection = collection;
                loadedCollectionName = collection.gameObject.name;
                collectionMenuNameField?.SetValueWithoutNotify(collectionMenuName);
                if (parameterField != null) parameterField.SetValueWithoutNotify(parameterName);
                RefreshRows();
                Selection.activeGameObject = collection.gameObject;
                EditorGUIUtility.PingObject(collection.gameObject);
                SetFeedback("已保存 " + ordered.Count + " 套衣服。默认穿着：" + roots[defaultParameterValue].name +
                    (usePlaceholderOutfit ? "。切换任何衣服时都会关闭占位衣服" : "") +
                    "。原衣服和分件菜单位置未改变。");
            }
            catch (Exception error)
            {
                WardrobeDiagnostics.Record("衣柜", "生成异常", error.ToString());
                Undo.RevertAllDownToGroup(undoGroup);
                Debug.LogException(error);
                SetFeedback("已撤销本次更新。" + (error is InvalidOperationException ? error.Message : "请查看控制台。"));
            }
        }

        private static void CheckExtraReferences(IEnumerable<GameObject> extras, IEnumerable<GameObject> removedNodes)
        {
            var removed = new HashSet<UnityEngine.Object>();
            foreach (var node in removedNodes)
                foreach (var component in node.GetComponentsInChildren<Component>(true))
                    if (component != null) { removed.Add(component); removed.Add(component.gameObject); }
            if (removed.Count == 0) return;
            foreach (var extra in extras)
                foreach (var component in extra.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue;
                    var property = new SerializedObject(component).GetIterator();
                    while (property.Next(true))
                        if (property.propertyType == SerializedPropertyType.ObjectReference &&
                            property.objectReferenceValue != null && removed.Contains(property.objectReferenceValue))
                            throw new InvalidOperationException("外加节点「" + extra.name +
                                "」引用了将被移除的衣服菜单项，请先调整引用。外加内容未删除。");
                }
        }

        private static GameObject CreateChild(Transform parent, string name)
        {
            var node = new GameObject(name);
            if (node.scene != parent.gameObject.scene)
                SceneManager.MoveGameObjectToScene(node, parent.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(node, "创建衣服总菜单节点");
            Undo.SetTransformParent(node.transform, parent, "设置衣服总菜单层级");
            node.transform.localPosition = Vector3.zero;
            node.transform.localRotation = Quaternion.identity;
            node.transform.localScale = Vector3.one;
            return node;
        }


        private static string NewParameterName()
        {
            return "Zincat/Wardrobe_" + Guid.NewGuid().ToString("N").Substring(0, 12);
        }
    }
}
#endif
