#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Zincat.AvatarSplitTest
{
    [CustomEditor(typeof(AvatarSplitConfig))]
    internal sealed class AvatarSplitConfigInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("此节点保存衣服分组、衣服引用和蓝图号。请随整个 Avatar 一起复制或导出。配置不会参与上传。", MessageType.Info);
            if (GUILayout.Button("打开模型拆分窗口", GUILayout.Height(30))) AvatarSplitWindow.Open();
            EditorGUILayout.HelpBox("复制 Avatar 会保留蓝图号。需要新建线上 Avatar 时，请在分组的“蓝图与检查”中清空蓝图号。", MessageType.None);
        }
    }
}
#endif
