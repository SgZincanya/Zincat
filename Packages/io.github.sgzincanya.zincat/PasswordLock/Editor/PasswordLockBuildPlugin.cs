using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(Zincat.PasswordLock.Editor.PasswordLockBuildPlugin))]

namespace Zincat.PasswordLock.Editor
{
    public sealed class PasswordLockBuildPlugin : Plugin<PasswordLockBuildPlugin>
    {
        public override string QualifiedName => "zincat.password-lock";
        public override string DisplayName => "Zincat 密码锁";

        protected override void Configure()
        {
            InPhase(BuildPhase.Generating).BeforePlugin("nadena.dev.modular-avatar")
                .Run("生成 MA 菜单与密码锁", context =>
                {
                    PasswordLockBuildFilter.Process(context.AvatarRootObject,
                        asset => context.AssetSaver.SaveAsset(asset));
                });
            InPhase(BuildPhase.Optimizing).Run("移除编辑器配置", context =>
            {
                foreach (var config in context.AvatarRootObject.GetComponentsInChildren<ZincatPasswordLock>(true).ToArray())
                    Object.DestroyImmediate(config);
            });
        }
    }
}
