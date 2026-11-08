#if UNITY_EDITOR
using UnityEditor;
namespace Zincat.AvatarSplitTest
{
 // Retain only the old window type so saved layouts close it cleanly after migration.
 // No menu item or separate feature entry remains.
 public sealed class CauExtrasWindow:EditorWindow
 {
  void OnEnable(){EditorApplication.delayCall+=CloseLegacy;}
  void OnDisable(){EditorApplication.delayCall-=CloseLegacy;}
  void CloseLegacy(){if(this)Close();}
 }
}
#endif
