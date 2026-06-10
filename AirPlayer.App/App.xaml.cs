using Microsoft.UI.Xaml;

namespace AirPlayer.App
{
    /// <summary>
    /// 提供特定于应用程序的行为，以补充默认的 Application 类。
    /// </summary>
    public partial class App : Application
    {
        // 应用程序主窗口实例
        private Window? m_window;

        /// <summary>
        /// 初始化单例应用程序对象。这是执行的创作代码的第一行，
        /// 逻辑上等同于 main() 或 WinMain()。
        /// </summary>
        public App()
        {
            // 初始化 XAML 组件
            this.InitializeComponent();
        }

        /// <summary>
        /// 在应用程序正常启动时调用。其他入口点
        /// 将在启动应用程序以打开特定文件等情况下使用。
        /// </summary>
        /// <param name="args">有关启动请求和过程的详细信息。</param>
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // 创建并激活主窗口
            m_window = new MainWindow();
            m_window.Activate();
        }
    }
}
