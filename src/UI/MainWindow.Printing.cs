using System.Threading.Tasks;
using System.Windows;
using Qingye.Core;
namespace Qingye.UI
{
    // Printing orchestration lives in a partial file so MainWindow keeps its
    // region-level overview within the size budget enforced by UiTests.
    public partial class MainWindow
    {
        internal async Task<bool> PreparePrintAsync()
        {
            var doc=Current;if(doc==null){SetNotice("没有可打印的文档");return false;}
            if(showPreview && preview.ShownDocument==doc && preview.CanPrint)return true;
            var profile=FilePresentation.For(doc);
            if(doc.Kind==DocumentKind.Text && profile.CanRead && documentBar.readViewButton.IsEnabled)
            {
                SetViewMode("reader");
                await UpdatePreviewAsync();
                if(showPreview && preview.ShownDocument==doc && preview.CanPrint)return true;
            }
            SetNotice("当前视图没有可打印内容；图片、PDF、网页与 EPUB 请用系统关联软件打开后打印。");
            return false;
        }
        async Task PrintCurrentAsync(){if(await PreparePrintAsync())preview.Print(this);}
    }
}
