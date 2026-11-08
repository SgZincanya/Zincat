namespace Zincat.PasswordLock.Editor
{
    internal static class PasswordLockPlayPreview
    {
        internal static void DrawPanel()
        {
            PasswordLockUi.Ensure();
            PasswordLockUi.BeginPanel("Unity Play · 密码锁已跳过");
            PasswordLockUi.Note("插件在 Play 构建时自动跳过密码锁菜单、FX 和 HUD，可以直接测试 MMD；其他 MA 模块照常处理。\n\n上传构建时会自动加入密码锁，无需手动开关。验证密码输入请使用 SDK Build & Test 或进入 VRChat。");
            PasswordLockUi.EndPanel();
        }
    }
}
