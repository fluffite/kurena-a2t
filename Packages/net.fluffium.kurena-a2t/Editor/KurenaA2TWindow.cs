using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Fluffium.KurenaA2T
{
    /// <summary>
    /// くれなちゃん向けの計測値。A ポーズの衣装を T ポーズに合わせるときの上腕の回転（親空間）。
    /// </summary>
    internal static class BuiltinRotations
    {
        public static readonly Quaternion LeftArm = new Quaternion(0.2505162f, 0.06436123f, -0.009133102f, 0.9659275f);
        public static readonly Quaternion RightArm = new Quaternion(0.250517458f, -0.06436173f, 0.009133262f, 0.9659271f);
    }

    internal sealed class KurenaA2TWindow : EditorWindow
    {
        const string ToolName = "Kurena-chan's A to T Adapter";

        enum RotationSource
        {
            Builtin,
            FromArmatures,
            Manual,
        }

        static readonly GUIContent[] RotationSourceLabels =
        {
            new GUIContent("ビルトイン（くれなちゃん）", "くれなちゃん向けの計測値（約 30°）を使います"),
            new GUIContent("アーマチュアの差分", "回転前・回転後の 2 つのアーマチュアから腕の回転の差分を求めます"),
            new GUIContent("直接入力", "親ボーン空間でのオイラー角を直接入力します"),
        };

        [SerializeField] Transform _outfitRoot;
        [SerializeField] bool _meshFoldout;
        [SerializeField] Transform _leftOverride;
        [SerializeField] Transform _rightOverride;
        [SerializeField] RotationSource _source = RotationSource.Builtin;
        [SerializeField] Transform _beforeArmature;
        [SerializeField] Transform _afterArmature;
        [SerializeField] Vector3 _manualLeft = BuiltinRotations.LeftArm.eulerAngles;
        [SerializeField] Vector3 _manualRight = BuiltinRotations.RightArm.eulerAngles;
        [SerializeField] bool _replace = true;
        [SerializeField] bool _backup = true;
        [SerializeField] bool _singleFile;
        // 処理対象のメッシュのうち、ユーザーが初期値から切り替えたもの
        [SerializeField] List<SkinnedMeshRenderer> _checkedMeshes = new List<SkinnedMeshRenderer>();
        [SerializeField] List<SkinnedMeshRenderer> _uncheckedMeshes = new List<SkinnedMeshRenderer>();

        readonly AdapterPreview _preview = new AdapterPreview();
        Vector2 _scroll;

        // 腕のボーンにウェイトがあるか（メッシュのチェックの初期値）の計算結果
        readonly Dictionary<string, bool> _weightCache = new Dictionary<string, bool>();

        // 直接入力に切り替えたときの初期値にするため、直前に表示した回転量を覚えておく
        Vector3 _shownLeft;
        Vector3 _shownRight;

        [MenuItem("Tools/FluffLabs/" + ToolName)]
        static void OpenFromToolsMenu()
        {
            Open();
        }

        // ヒエラルキーの右クリックメニューから、選択したオブジェクトを衣装のルートとして開く
        [MenuItem("GameObject/FluffLabs/" + ToolName)]
        static void OpenFromHierarchy(MenuCommand command)
        {
            var target = command.context as GameObject;
            if (target == null) target = Selection.activeGameObject;
            var window = Open();
            if (target != null) window.SetOutfitRoot(target.transform);
        }

        [MenuItem("GameObject/FluffLabs/" + ToolName, true)]
        static bool ValidateOpenFromHierarchy()
        {
            var target = Selection.activeGameObject;
            return target != null && !EditorUtility.IsPersistent(target);
        }

        static KurenaA2TWindow Open()
        {
            var isNew = !HasOpenInstances<KurenaA2TWindow>();
            var window = GetWindow<KurenaA2TWindow>();
            window.titleContent = new GUIContent(ToolName);
            window.minSize = new Vector2(440, 300);
            if (isNew)
            {
                // 初回はエディタのメインウィンドウの中央に開く
                var size = new Vector2(754, 420);
                var main = EditorGUIUtility.GetMainWindowPosition();
                window.position = new Rect(main.center - size / 2, size);
            }
            window.Show();
            return window;
        }

        void SetOutfitRoot(Transform outfitRoot)
        {
            if (outfitRoot == _outfitRoot) return;
            _preview.Hide();
            _outfitRoot = outfitRoot;
            ResetOutfitSettings();
            Repaint();
        }

        // 衣装を変えたら、前の衣装に対するオーバーライドとメッシュの選択は破棄する
        void ResetOutfitSettings()
        {
            _leftOverride = null;
            _rightOverride = null;
            _checkedMeshes.Clear();
            _uncheckedMeshes.Clear();
        }

        void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            if (_outfitRoot == null && Selection.activeTransform != null && !EditorUtility.IsPersistent(Selection.activeTransform))
            {
                _outfitRoot = Selection.activeTransform;
            }
        }

        void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            _preview.Hide();
        }

        void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode) _preview.Hide();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUI.BeginChangeCheck();

            var targetError = DrawTargetSection(out var leftArm, out var rightArm, out var renderers);
            EditorGUILayout.Space();
            var rotationError = DrawRotationSection(leftArm, rightArm, out var arms);
            var error = targetError ?? rotationError;

            EditorGUILayout.Space();
            DrawProcessSection();

            var changed = EditorGUI.EndChangeCheck();

            if (error != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(error, MessageType.Warning);
            }

            // 入力が変わったらプレビューを作り直す。入力が不正になったらプレビューを止める
            if (_preview.IsActive && (changed || error != null))
            {
                if (error == null) _preview.Show(renderers, arms);
                else _preview.Hide();
            }

            EditorGUILayout.Space();
            DrawActionSection(arms, renderers, error);

            EditorGUILayout.EndScrollView();
        }

        // ---- 処理対象 ----

        string DrawTargetSection(out Transform leftArm, out Transform rightArm, out List<SkinnedMeshRenderer> renderers)
        {
            leftArm = null;
            rightArm = null;
            renderers = new List<SkinnedMeshRenderer>();

            EditorGUILayout.LabelField("処理対象", EditorStyles.boldLabel);
            var newRoot = (Transform)EditorGUILayout.ObjectField("衣装のゲームオブジェクト", _outfitRoot, typeof(Transform), true);
            if (newRoot != _outfitRoot)
            {
                _outfitRoot = newRoot;
                ResetOutfitSettings();
            }

            if (_outfitRoot == null) return "衣装のゲームオブジェクトを指定してください。";
            if (EditorUtility.IsPersistent(_outfitRoot)) return "シーン内の衣装を指定してください（プレハブアセットは指定できません）。";
            var rootAnimator = _outfitRoot.GetComponent<Animator>();
            if (rootAnimator != null && rootAnimator.isHuman) return "アバター本体ではなく、衣装のゲームオブジェクトを指定してください。";

            var leftDetected = ArmDetector.Detect(_outfitRoot, true);
            var rightDetected = ArmDetector.Detect(_outfitRoot, false);

            _leftOverride = DrawOverrideField("Left arm", _leftOverride, leftDetected);
            _rightOverride = DrawOverrideField("Right arm", _rightOverride, rightDetected);

            leftArm = _leftOverride != null ? _leftOverride : leftDetected;
            rightArm = _rightOverride != null ? _rightOverride : rightDetected;

            if (_leftOverride != null && EditorUtility.IsPersistent(_leftOverride)
                || _rightOverride != null && EditorUtility.IsPersistent(_rightOverride))
            {
                return "オーバーライドには、シーン内のボーンを指定してください（プレハブアセットは指定できません）。";
            }
            if (leftArm == null && rightArm == null)
            {
                return "腕ボーンを検出できませんでした。オーバーライドで指定してください。";
            }
            if (leftArm != null && rightArm != null
                && (leftArm == rightArm || leftArm.IsChildOf(rightArm) || rightArm.IsChildOf(leftArm)))
            {
                return "Left arm と Right arm には、互いに親子関係のない別々のボーンを指定してください。";
            }

            var armBones = new List<Transform>();
            if (leftArm != null) armBones.Add(leftArm);
            if (rightArm != null) armBones.Add(rightArm);
            return DrawMeshSelection(armBones, renderers);
        }

        string DrawMeshSelection(List<Transform> armBones, List<SkinnedMeshRenderer> selected)
        {
            var candidates = BindposeAdapter.FindAffectedRenderers(_outfitRoot, armBones);
            if (candidates.Count == 0) return "腕ボーンを使っているメッシュが衣装の中に見つかりません。";

            // 初期値は、腕のボーンにウェイトがあるメッシュだけオン
            var weighted = new bool[candidates.Count];
            var isChecked = new bool[candidates.Count];
            var checkedCount = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                weighted[i] = HasArmWeights(candidates[i], armBones);
                isChecked[i] = _checkedMeshes.Contains(candidates[i]) || weighted[i] && !_uncheckedMeshes.Contains(candidates[i]);
                if (isChecked[i]) checkedCount++;
            }

            _meshFoldout = EditorGUILayout.Foldout(_meshFoldout, $"処理対象のメッシュ ({checkedCount})", true);
            if (_meshFoldout)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    for (var i = 0; i < candidates.Count; i++)
                    {
                        var renderer = candidates[i];
                        var label = weighted[i] ? renderer.name : renderer.name + "（腕のウェイトなし）";
                        var newChecked = EditorGUILayout.ToggleLeft(label, isChecked[i]);
                        if (newChecked == isChecked[i]) continue;

                        isChecked[i] = newChecked;
                        _checkedMeshes.Remove(renderer);
                        _uncheckedMeshes.Remove(renderer);
                        if (newChecked != weighted[i]) (newChecked ? _checkedMeshes : _uncheckedMeshes).Add(renderer);
                    }
                }
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                if (isChecked[i]) selected.Add(candidates[i]);
            }
            return selected.Count == 0 ? "処理対象のメッシュを 1 つ以上選んでください。" : null;
        }

        bool HasArmWeights(SkinnedMeshRenderer renderer, List<Transform> armBones)
        {
            var key = renderer.GetInstanceID() + ":" + renderer.sharedMesh.GetInstanceID();
            foreach (var bone in armBones) key += ":" + bone.GetInstanceID();
            if (!_weightCache.TryGetValue(key, out var weighted))
            {
                weighted = BindposeAdapter.HasWeightsOnBones(renderer, armBones);
                _weightCache[key] = weighted;
            }
            return weighted;
        }

        // 自動検出と異なるボーンでオーバーライドしているときの入力欄の色
        static readonly Color OverriddenFieldColor = new Color(1f, 0.7f, 0.3f);

        /// <summary>
        /// 腕ボーンの入力欄。普段は自動検出の結果を表示し、別のボーンを入れるとオーバーライドになる。
        /// </summary>
        Transform DrawOverrideField(string label, Transform overrideValue, Transform detected)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var shown = overrideValue != null ? overrideValue : detected;
                var backgroundColor = GUI.backgroundColor;
                if (overrideValue != null) GUI.backgroundColor = OverriddenFieldColor;
                var picked = (Transform)EditorGUILayout.ObjectField(label, shown, typeof(Transform), true);
                GUI.backgroundColor = backgroundColor;
                if (picked != shown) overrideValue = picked == detected ? null : picked;

                using (new EditorGUI.DisabledScope(overrideValue == null))
                {
                    if (GUILayout.Button("自動検出", GUILayout.Width(72))) overrideValue = null;
                }
            }
            if (overrideValue != null)
            {
                var message = "自動検出とは異なるボーンを指定しています。通常、変更する必要はありません。";
                if (!IsDescendantOfOutfit(overrideValue)) message += "\nこのボーンは衣装のゲームオブジェクトの外にあります。";
                EditorGUILayout.HelpBox(message, MessageType.Warning);
            }
            return overrideValue;
        }

        bool IsDescendantOfOutfit(Transform t)
        {
            return t != _outfitRoot && t.IsChildOf(_outfitRoot);
        }

        // ---- 回転量 ----

        string DrawRotationSection(Transform leftArm, Transform rightArm, out List<ArmRotation> arms)
        {
            arms = new List<ArmRotation>();

            EditorGUILayout.LabelField("回転量", EditorStyles.boldLabel);
            var newSource = (RotationSource)GUILayout.Toolbar((int)_source, RotationSourceLabels);
            if (newSource != _source)
            {
                if (newSource == RotationSource.Manual)
                {
                    _manualLeft = _shownLeft;
                    _manualRight = _shownRight;
                }
                _source = newSource;
            }

            if (_source == RotationSource.FromArmatures)
            {
                _beforeArmature = (Transform)EditorGUILayout.ObjectField("回転前のアーマチュア", _beforeArmature, typeof(Transform), true);
                _afterArmature = (Transform)EditorGUILayout.ObjectField("回転後のアーマチュア", _afterArmature, typeof(Transform), true);
            }

            var leftError = ResolveDelta(leftArm, BuiltinRotations.LeftArm, _manualLeft, out var leftDelta);
            var rightError = ResolveDelta(rightArm, BuiltinRotations.RightArm, _manualRight, out var rightDelta);

            // どの方式でも、実際に適用される回転を直接入力と同じ欄に表示する（直接入力以外では編集不可）
            var manual = _source == RotationSource.Manual;
            using (new EditorGUI.DisabledScope(!manual))
            {
                var left = DrawRotationField("Left arm", leftArm, manual ? _manualLeft : WrapEuler(leftDelta), leftError == null);
                var right = DrawRotationField("Right arm", rightArm, manual ? _manualRight : WrapEuler(rightDelta), rightError == null);
                if (manual)
                {
                    _manualLeft = left;
                    _manualRight = right;
                }
                if (leftError == null) _shownLeft = left;
                if (rightError == null) _shownRight = right;
            }

            switch (_source)
            {
                case RotationSource.Builtin:
                    EditorGUILayout.HelpBox("くれなちゃん向けの計測値です。くれなちゃんで、A ポーズの衣装を T ポーズに合わせるときの上腕の回転量です。", MessageType.None);
                    break;
                case RotationSource.FromArmatures:
                    EditorGUILayout.HelpBox("それぞれのアーマチュアから処理対象と同じ名前のボーンを探し、その回転の差分を使います。", MessageType.None);
                    break;
                case RotationSource.Manual:
                    EditorGUILayout.HelpBox("親ボーン（肩）の空間でのオイラー角です。", MessageType.None);
                    if (GUILayout.Button("ビルトイン値を読み込む"))
                    {
                        _manualLeft = BuiltinRotations.LeftArm.eulerAngles;
                        _manualRight = BuiltinRotations.RightArm.eulerAngles;
                        GUI.changed = true;
                    }
                    break;
            }

            var error = leftError ?? rightError;
            if (error != null) return error;

            if (leftArm != null) arms.Add(new ArmRotation(leftArm, leftDelta));
            if (rightArm != null) arms.Add(new ArmRotation(rightArm, rightDelta));
            return null;
        }

        static Vector3 DrawRotationField(string label, Transform arm, Vector3 euler, bool resolved)
        {
            Vector3 result;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(arm == null || !resolved))
                {
                    result = EditorGUILayout.Vector3Field(label, resolved ? euler : Vector3.zero);
                }
                var angle = resolved && arm != null ? $"{Quaternion.Angle(Quaternion.identity, Quaternion.Euler(result)):F2}°" : "—";
                GUILayout.Label(angle, GUILayout.Width(60));
            }
            return resolved ? result : euler;
        }

        string ResolveDelta(Transform arm, Quaternion builtin, Vector3 manual, out Quaternion delta)
        {
            delta = Quaternion.identity;
            if (arm == null) return null;

            switch (_source)
            {
                case RotationSource.Builtin:
                    delta = builtin;
                    return null;

                case RotationSource.Manual:
                    delta = Quaternion.Euler(manual);
                    return null;

                case RotationSource.FromArmatures:
                    if (_beforeArmature == null || _afterArmature == null) return "回転前・回転後のアーマチュアを指定してください。";
                    var before = FindDescendant(_beforeArmature, arm.name);
                    var after = FindDescendant(_afterArmature, arm.name);
                    if (before == null || after == null) return $"アーマチュアの中に「{arm.name}」が見つかりません。";
                    delta = after.localRotation * Quaternion.Inverse(before.localRotation);
                    return null;
            }
            return null;
        }

        static Transform FindDescendant(Transform root, string name)
        {
            return root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
        }

        static Vector3 WrapEuler(Quaternion q)
        {
            var e = q.eulerAngles;
            return new Vector3(Wrap(e.x), Wrap(e.y), Wrap(e.z));
        }

        static float Wrap(float angle)
        {
            return angle > 180f ? angle - 360f : angle;
        }

        // ---- 追加の処理 ----

        void DrawProcessSection()
        {
            EditorGUILayout.LabelField("追加の処理", EditorStyles.boldLabel);
            _replace = EditorGUILayout.ToggleLeft("保存後に衣装オブジェクトに反映する", _replace);
            using (new EditorGUI.DisabledScope(!_replace))
            using (new EditorGUI.IndentLevelScope())
            {
                _backup = EditorGUILayout.ToggleLeft("反映前の衣装オブジェクトのバックアップを残す", _backup);
            }
            _singleFile = EditorGUILayout.ToggleLeft("複数のメッシュを１ファイルにまとめる", _singleFile);

            EditorGUILayout.HelpBox(_replace
                ? "衣装オブジェクトへの反映は Undo（Ctrl+Z）で元に戻せます。保存したファイルは Undo 後も残ります。"
                : "メッシュをファイルに保存するだけで、シーンは変更しません。", MessageType.None);
        }

        // ---- プレビュー・保存 ----

        void DrawActionSection(List<ArmRotation> arms, List<SkinnedMeshRenderer> renderers, string error)
        {
            // ラベルが変わってもボタンの幅が変わらないよう、2 つのボタンを同じ固定幅にする
            const float gap = 16f;
            var buttonWidth = Mathf.Max(80f, (EditorGUIUtility.currentViewWidth - gap - 32f) / 2f);
            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(error != null))
            {
                var preview = GUILayout.Toggle(_preview.IsActive, "プレビュー", "Button", GUILayout.Height(28), GUILayout.Width(buttonWidth));
                if (preview != _preview.IsActive)
                {
                    if (preview) _preview.Show(renderers, arms);
                    else _preview.Hide();
                }

                GUILayout.Space(gap);

                var label = SavesMultipleFiles(renderers) ? "各メッシュを保存..." : "保存...";
                if (GUILayout.Button(label, GUILayout.Height(28), GUILayout.Width(buttonWidth)))
                {
                    Save(arms, renderers);
                    GUIUtility.ExitGUI();
                }
            }
            if (_preview.IsActive)
            {
                EditorGUILayout.HelpBox("プレビュー中です（Scene ビューのみ）。シーンには変更を加えていません。", MessageType.Info);
            }
        }

        void Save(List<ArmRotation> arms, List<SkinnedMeshRenderer> renderers)
        {
            GetDefaultSavePath(out var directory, out var baseName);

            List<Mesh> meshes;
            string savedPath;
            if (SavesMultipleFiles(renderers))
            {
                var selected = EditorUtility.SaveFolderPanel(
                    "変換したメッシュの保存先フォルダを選んでください（メッシュごとに .asset ファイルを保存）",
                    directory, "");
                if (string.IsNullOrEmpty(selected)) return;

                savedPath = ToProjectPath(selected);
                if (savedPath == null)
                {
                    EditorUtility.DisplayDialog(ToolName, "保存先には、このプロジェクトの Assets 以下のフォルダを選んでください。", "OK");
                    return;
                }

                _preview.Hide();
                meshes = BindposeAdapter.SaveMeshesSeparately(renderers, arms, savedPath);
            }
            else
            {
                // 1 ファイルにまとめるとき、またはメッシュが 1 つだけのとき
                var title = _singleFile
                    ? "変換したメッシュの保存先を選んでください（1 つのファイルにまとめて保存）"
                    : "変換したメッシュの保存先を選んでください";
                var fileName = _singleFile ? baseName : renderers[0].sharedMesh.name + "_A2T";
                savedPath = EditorUtility.SaveFilePanelInProject(title, fileName, "asset", "変換したメッシュの保存先を選んでください。", directory);
                if (string.IsNullOrEmpty(savedPath)) return;

                _preview.Hide();
                meshes = BindposeAdapter.SaveMeshes(renderers, arms, savedPath);
            }

            if (_replace) BindposeAdapter.ReplaceMeshes(_outfitRoot, renderers, meshes, _backup);

            EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(savedPath));
            EditorUtility.DisplayDialog(ToolName, $"{meshes.Count} 個のメッシュを変換して保存しました。\n{savedPath}", "OK");
        }

        /// <summary>
        /// メッシュごとに別々のファイルへ保存することになるか（1 ファイルにまとめず、メッシュが複数あるとき）。
        /// </summary>
        bool SavesMultipleFiles(List<SkinnedMeshRenderer> renderers)
        {
            return !_singleFile && renderers.Count > 1;
        }

        /// <summary>
        /// 絶対パスをプロジェクト相対の Assets パスに変換する（Assets の外なら null）。
        /// </summary>
        static string ToProjectPath(string absolutePath)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath)?.Replace('\\', '/') + "/";
            var path = absolutePath.Replace('\\', '/');
            if (!path.StartsWith(projectRoot, System.StringComparison.OrdinalIgnoreCase)) return null;
            path = path.Substring(projectRoot.Length);
            return path == "Assets" || path.StartsWith("Assets/") ? path : null;
        }

        /// <summary>
        /// 元の Prefab があれば「Prefab のフォルダ」と「Prefab 名_A2T」、なければ「Assets」と「衣装名_A2T」を初期値にする。
        /// 1 ファイルにまとめるときは、Prefab 名_A2T（衣装名_A2T）をファイル名に使う。
        /// </summary>
        void GetDefaultSavePath(out string directory, out string fileName)
        {
            var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(_outfitRoot.gameObject);
            if (!string.IsNullOrEmpty(prefabPath) && prefabPath.StartsWith("Assets/"))
            {
                directory = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/');
                fileName = Path.GetFileNameWithoutExtension(prefabPath) + "_A2T";
                return;
            }
            directory = "Assets";
            fileName = _outfitRoot.name + "_A2T";
        }
    }
}
