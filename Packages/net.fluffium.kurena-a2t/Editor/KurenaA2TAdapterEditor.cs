using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Fluffium.KurenaA2T
{
    [CustomEditor(typeof(KurenaA2TAdapter))]
    internal sealed class KurenaA2TAdapterEditor : Editor
    {
        const string ToolName = "Kurena-chan's A to T Adapter";
        const string BackupPrefKey = "net.fluffium.kurena-a2t.Backup";
        const string SingleFilePrefKey = "net.fluffium.kurena-a2t.SingleFile";

        static readonly GUIContent[] RotationSourceLabels =
        {
            new GUIContent("ビルトイン（くれなちゃん）", "くれなちゃん向けの計測値（約 30°）を使います"),
            new GUIContent("アーマチュアの差分", "回転前・回転後の 2 つのアーマチュアから腕の回転の差分を求めます"),
            new GUIContent("直接入力", "親ボーン空間でのオイラー角を直接入力します"),
        };

        // 自動検出と異なるボーンでオーバーライドしているときの入力欄の色
        static readonly Color OverriddenFieldColor = new Color(1f, 0.7f, 0.3f);

        bool _meshFoldout;
        bool _optionFoldout;

        // 直接入力に切り替えたときの初期値にするため、直前に表示した回転量を覚えておく
        Vector3 _shownLeft;
        Vector3 _shownRight;

        KurenaA2TAdapter Adapter => (KurenaA2TAdapter)target;

        public override void OnInspectorGUI()
        {
            var resolved = AdapterResolver.Resolve(Adapter);

            DrawTargetSection(resolved);
            EditorGUILayout.Space();
            DrawRotationSection(resolved);
            EditorGUILayout.Space();
            DrawPreviewSection();

            if (resolved.Error != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(resolved.Error, MessageType.Warning);
            }

            EditorGUILayout.Space();
            DrawApplyOption(resolved);
        }

        /// <summary>
        /// コンポーネントの値を Undo 可能な形で変更する。
        /// </summary>
        void Modify(string label, Action<KurenaA2TAdapter> change)
        {
            Undo.RecordObject(Adapter, label);
            change(Adapter);
            PrefabUtility.RecordPrefabInstancePropertyModifications(Adapter);
            EditorUtility.SetDirty(Adapter);
        }

        // ---- 処理対象 ----

        void DrawTargetSection(ResolvedAdapter resolved)
        {
            EditorGUILayout.LabelField("処理対象", EditorStyles.boldLabel);

            var left = DrawOverrideField("衣装のLeft arm", Adapter.leftArmOverride, resolved.LeftDetected);
            if (left != Adapter.leftArmOverride) Modify("衣装のLeft arm の変更", a => a.leftArmOverride = left);
            var right = DrawOverrideField("衣装のRight arm", Adapter.rightArmOverride, resolved.RightDetected);
            if (right != Adapter.rightArmOverride) Modify("衣装のRight arm の変更", a => a.rightArmOverride = right);

            if (resolved.Candidates.Count == 0) return;

            var checkedCount = 0;
            foreach (var isChecked in resolved.Checked)
            {
                if (isChecked) checkedCount++;
            }

            _meshFoldout = EditorGUILayout.Foldout(_meshFoldout, $"処理対象のメッシュ ({checkedCount})", true);
            if (!_meshFoldout) return;

            using (new EditorGUI.IndentLevelScope())
            {
                for (var i = 0; i < resolved.Candidates.Count; i++)
                {
                    var renderer = resolved.Candidates[i];
                    var weighted = resolved.Weighted[i];
                    var label = weighted ? renderer.name : renderer.name + "（腕のウェイトなし）";
                    var newChecked = EditorGUILayout.ToggleLeft(label, resolved.Checked[i]);
                    if (newChecked == resolved.Checked[i]) continue;

                    Modify("処理対象のメッシュの変更", a =>
                    {
                        a.checkedMeshes.Remove(renderer);
                        a.uncheckedMeshes.Remove(renderer);
                        if (newChecked != weighted) (newChecked ? a.checkedMeshes : a.uncheckedMeshes).Add(renderer);
                    });
                }
            }
        }

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
                if (!overrideValue.IsChildOf(Adapter.transform)) message += "\nこのボーンは衣装のゲームオブジェクトの外にあります。";
                EditorGUILayout.HelpBox(message, MessageType.Warning);
            }
            return overrideValue;
        }

        // ---- 回転量 ----

        void DrawRotationSection(ResolvedAdapter resolved)
        {
            EditorGUILayout.LabelField("回転量", EditorStyles.boldLabel);
            var newSource = (KurenaA2TAdapter.RotationSource)GUILayout.Toolbar((int)Adapter.rotationSource, RotationSourceLabels);
            if (newSource != Adapter.rotationSource)
            {
                var shownLeft = _shownLeft;
                var shownRight = _shownRight;
                Modify("回転量の指定方法の変更", a =>
                {
                    // 直接入力に切り替えたときは、直前に表示していた値をそのまま初期値にする
                    if (newSource == KurenaA2TAdapter.RotationSource.Manual)
                    {
                        a.manualLeft = shownLeft;
                        a.manualRight = shownRight;
                    }
                    a.rotationSource = newSource;
                });
            }

            if (Adapter.rotationSource == KurenaA2TAdapter.RotationSource.FromArmatures)
            {
                var before = (Transform)EditorGUILayout.ObjectField("回転前のアーマチュア", Adapter.beforeArmature, typeof(Transform), true);
                if (before != Adapter.beforeArmature) Modify("回転前のアーマチュアの変更", a => a.beforeArmature = before);
                var after = (Transform)EditorGUILayout.ObjectField("回転後のアーマチュア", Adapter.afterArmature, typeof(Transform), true);
                if (after != Adapter.afterArmature) Modify("回転後のアーマチュアの変更", a => a.afterArmature = after);
            }

            // どの方式でも、実際に適用される回転を直接入力と同じ欄に表示する（直接入力以外では編集不可）
            var manual = Adapter.rotationSource == KurenaA2TAdapter.RotationSource.Manual;
            using (new EditorGUI.DisabledScope(!manual))
            {
                var left = DrawRotationField("衣装のLeft arm", resolved.LeftArm, manual ? Adapter.manualLeft : WrapEuler(resolved.LeftDelta), resolved.LeftDelta.HasValue);
                var right = DrawRotationField("衣装のRight arm", resolved.RightArm, manual ? Adapter.manualRight : WrapEuler(resolved.RightDelta), resolved.RightDelta.HasValue);
                if (manual && (left != Adapter.manualLeft || right != Adapter.manualRight))
                {
                    Modify("回転量の変更", a =>
                    {
                        a.manualLeft = left;
                        a.manualRight = right;
                    });
                }
                if (resolved.LeftDelta.HasValue) _shownLeft = left;
                if (resolved.RightDelta.HasValue) _shownRight = right;
            }

            switch (Adapter.rotationSource)
            {
                case KurenaA2TAdapter.RotationSource.Builtin:
                    EditorGUILayout.HelpBox("くれなちゃん向けの計測値です。くれなちゃんで、A ポーズの衣装を T ポーズに合わせるときの上腕の回転量です。", MessageType.None);
                    break;
                case KurenaA2TAdapter.RotationSource.FromArmatures:
                    EditorGUILayout.HelpBox("それぞれのアーマチュアから処理対象と同じ名前のボーンを探し、その回転の差分を使います。", MessageType.None);
                    break;
                case KurenaA2TAdapter.RotationSource.Manual:
                    EditorGUILayout.HelpBox("親ボーン（肩）の空間でのオイラー角です。", MessageType.None);
                    if (GUILayout.Button("ビルトイン値を読み込む"))
                    {
                        Modify("ビルトイン値の読み込み", a =>
                        {
                            a.manualLeft = BuiltinRotations.LeftArmEuler;
                            a.manualRight = BuiltinRotations.RightArmEuler;
                        });
                    }
                    break;
            }
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

        static Vector3 WrapEuler(Quaternion? q)
        {
            if (!q.HasValue) return Vector3.zero;
            var e = q.Value.eulerAngles;
            return new Vector3(Wrap(e.x), Wrap(e.y), Wrap(e.z));
        }

        static float Wrap(float angle)
        {
            return angle > 180f ? angle - 360f : angle;
        }

        // ---- プレビュー ----

        static void DrawPreviewSection()
        {
            EditorGUILayout.HelpBox("Scene ビューとビルド時に、メッシュのアセットを変更せずに T ポーズで反映します（NDMF）。", MessageType.Info);
            var enabled = AdapterPreviewFilter.PreviewNode.IsEnabled.Value;
            var newEnabled = EditorGUILayout.ToggleLeft("Scene ビューでプレビューする", enabled);
            if (newEnabled != enabled) AdapterPreviewFilter.PreviewNode.IsEnabled.Value = newEnabled;
        }

        // ---- オプション：メッシュをアセットとして出力して適用 ----

        void DrawApplyOption(ResolvedAdapter resolved)
        {
            _optionFoldout = EditorGUILayout.Foldout(_optionFoldout, "オプション：メッシュをアセットとして出力して適用", true);
            if (!_optionFoldout) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.HelpBox(
                    "変換したメッシュを .asset ファイルとして保存して衣装に割り当て、このコンポーネントを取り除きます。" +
                    "Undo（Ctrl+Z）で元に戻せます。保存したファイルは Undo 後も残ります。", MessageType.None);

                var backup = EditorGUILayout.ToggleLeft("反映前の衣装オブジェクトのバックアップを残す", EditorPrefs.GetBool(BackupPrefKey, true));
                EditorPrefs.SetBool(BackupPrefKey, backup);
                if (backup && !BindposeAdapter.IsVerifiedUnityVersion)
                {
                    EditorGUILayout.HelpBox(
                        $"バックアップの複製は Unity {BindposeAdapter.VerifiedUnityVersion} で動作を確認した方法で行っています。" +
                        $"現在の Unity（{Application.unityVersion}）では、Prefab とのつながりを保てない場合があります。",
                        MessageType.Warning);
                }

                var singleFile = EditorGUILayout.ToggleLeft("複数のメッシュを１ファイルにまとめる", EditorPrefs.GetBool(SingleFilePrefKey, false));
                EditorPrefs.SetBool(SingleFilePrefKey, singleFile);

                var multipleFiles = !singleFile && resolved.Renderers.Count > 1;
                using (new EditorGUI.DisabledScope(resolved.Error != null))
                {
                    if (GUILayout.Button(multipleFiles ? "各メッシュを保存して適用..." : "保存して適用...", GUILayout.Height(28)))
                    {
                        Apply(resolved, backup, singleFile, multipleFiles);
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        void Apply(ResolvedAdapter resolved, bool backup, bool singleFile, bool multipleFiles)
        {
            var adapter = Adapter;
            var outfitRoot = adapter.transform;
            GetDefaultSavePath(outfitRoot, out var directory, out var baseName);

            List<Mesh> meshes;
            string savedPath;
            if (multipleFiles)
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
                meshes = BindposeAdapter.SaveMeshesSeparately(resolved.Renderers, resolved.Arms, savedPath);
            }
            else
            {
                // 1 ファイルにまとめるとき、またはメッシュが 1 つだけのとき
                var title = singleFile
                    ? "変換したメッシュの保存先を選んでください（1 つのファイルにまとめて保存）"
                    : "変換したメッシュの保存先を選んでください";
                var fileName = singleFile ? baseName : resolved.Renderers[0].sharedMesh.name + "_A2T";
                savedPath = EditorUtility.SaveFilePanelInProject(title, fileName, "asset", "変換したメッシュの保存先を選んでください。", directory);
                if (string.IsNullOrEmpty(savedPath)) return;

                meshes = BindposeAdapter.SaveMeshes(resolved.Renderers, resolved.Arms, savedPath);
            }

            // メッシュの差し替え・バックアップ・コンポーネントの削除を 1 回の Undo で戻せるようにまとめる
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(ToolName);
            var group = Undo.GetCurrentGroup();
            BindposeAdapter.ReplaceMeshes(outfitRoot, resolved.Renderers, meshes, backup);
            Undo.DestroyObjectImmediate(adapter);
            Undo.CollapseUndoOperations(group);

            EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(savedPath));
            EditorUtility.DisplayDialog(ToolName, $"{meshes.Count} 個のメッシュを変換して保存し、衣装に適用しました。\n{savedPath}", "OK");
        }

        /// <summary>
        /// 絶対パスをプロジェクト相対の Assets パスに変換する（Assets の外なら null）。
        /// </summary>
        static string ToProjectPath(string absolutePath)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath)?.Replace('\\', '/') + "/";
            var path = absolutePath.Replace('\\', '/');
            if (!path.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)) return null;
            path = path.Substring(projectRoot.Length);
            return path == "Assets" || path.StartsWith("Assets/") ? path : null;
        }

        /// <summary>
        /// 元の Prefab があれば「Prefab のフォルダ」と「Prefab 名_A2T」、なければ「Assets」と「衣装名_A2T」を初期値にする。
        /// 1 ファイルにまとめるときは、Prefab 名_A2T（衣装名_A2T）をファイル名に使う。
        /// </summary>
        static void GetDefaultSavePath(Transform outfitRoot, out string directory, out string fileName)
        {
            var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(outfitRoot.gameObject);
            if (!string.IsNullOrEmpty(prefabPath) && prefabPath.StartsWith("Assets/"))
            {
                directory = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/');
                fileName = Path.GetFileNameWithoutExtension(prefabPath) + "_A2T";
                return;
            }
            directory = "Assets";
            fileName = outfitRoot.name + "_A2T";
        }
    }
}
