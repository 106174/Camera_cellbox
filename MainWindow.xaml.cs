using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Windows.Forms; // 注意是 WinForms 的 FolderBrowserDialog
using LiveCharts;
using LiveCharts.Wpf;
using LiveCharts.Defaults;
using LiveCharts.Configurations;
using System.Collections.Generic;
using System.Windows.Threading;
using System.Windows.Controls;

namespace Camera_Organoids
{
    public partial class MainWindow : System.Windows.Window
    {
        private VideoCapture _capture;
        private Mat _frame;
        private Thread _cameraThread;
        private bool _isRunning;
        private string _saveFolderPath = "";        //截图保存路径
        private volatile bool isHoming = false;     // 电机归位判断
        private Mat originalImage;
        private readonly object _frameLock = new object();
        private readonly object _enhancerLock = new object();    // 保护 _enhancer 参数读取/写入
        private ImageEnhancer _enhancer = new ImageEnhancer();

        // 声明串口对象
        private SerialPort serialPort;
        // 字段声明
        public ChartValues<DateTimePoint> Co2Values = new ChartValues<DateTimePoint>();
        public ChartValues<DateTimePoint> TempValues = new ChartValues<DateTimePoint>();
        public Func<double, string> TimeFormatter = value =>
        {
            try
            {
                // LiveCharts X轴的值是DateTime.Ticks
                var dt = new DateTime((long)value);
                return dt.ToString("HH:mm:ss");
            }
            catch
            {
                return "";
            }
        };

        public MainWindow()
        {

            // —— VERY IMPORTANT —— 在 InitializeComponent 之前注册 DateTimePoint 映射
            var dayConfig = Mappers.Xy<DateTimePoint>()
                .X(dp => dp.DateTime.Ticks)   // X 使用 Ticks
                .Y(dp => dp.Value);           // Y 使用值
            Charting.For<DateTimePoint>(dayConfig);

            // 现在再实例化 XAML 中的控件
            InitializeComponent();

            // 绑定上下文
            DataContext = this;

            StartCamera();
            RefreshPorts();

            // 初始化 Series（图例/曲线）
            Co2Chart.Series = new SeriesCollection
            {
                new LineSeries { Title = "CO2", Values = Co2Values, PointGeometry = null }
            };

            TempChart.Series = new SeriesCollection
            {
                new LineSeries { Title = "温度", Values = TempValues, PointGeometry = null }
            };

            // 确保 X 轴使用时间格式（直接在代码设置，避免 binding 时机问题）
            if (Co2Chart.AxisX.Count == 0)
                Co2Chart.AxisX.Add(new Axis { Title = "时间", LabelFormatter = TimeFormatter });
            else
                Co2Chart.AxisX[0].LabelFormatter = TimeFormatter;

            if (TempChart.AxisX.Count == 0)
                TempChart.AxisX.Add(new Axis { Title = "时间", LabelFormatter = TimeFormatter });
            else
                TempChart.AxisX[0].LabelFormatter = TimeFormatter;
        }


        private DispatcherTimer _autoCaptureTimer;

        private void StartCamera()
        {
            _capture = new VideoCapture(0);
            _frame = new Mat();
            _isRunning = true;

            _cameraThread = new Thread(() =>
            {
                while (_isRunning)
                {
                    _capture.Read(_frame);
                    if (_frame.Empty()) continue;

                    // 先制作一份线程安全的副本（避免其它线程改动原始 _frame）
                    Mat frameCopy;
                    lock (_frameLock)
                    {
                        frameCopy = _frame.Clone(); // 深拷贝
                    }

                    // 调用增强（在摄像头线程执行）
                    Mat enhancedFrame;
                    // 读取 enhancer 参数为局部副本（避免竞态） - 实现位于 ImageEnhancer 内也有同样防护，但这里更保险
                    lock (_enhancerLock)
                    {
                        enhancedFrame = _enhancer.Enhance(frameCopy);
                    }

                    // 转换并显示（BitmapSource 是 UI 线程可安全使用的）
                    var image = enhancedFrame.ToBitmapSource();
                    image.Freeze();
                    Dispatcher.Invoke(() =>
                    {
                        CameraView.Source = image;
                    });

                    // 释放资源
                    enhancedFrame.Dispose();
                    frameCopy.Dispose();

                    Thread.Sleep(30);
                }
            });
            _cameraThread.IsBackground = true;
            _cameraThread.Start();

            // 自动拍照定时器（保持不变）
            _autoCaptureTimer = new DispatcherTimer();
            _autoCaptureTimer.Interval = TimeSpan.FromSeconds(300); // 每5分钟拍一张
            _autoCaptureTimer.Tick += (s, e) => AutoCapture();
            _autoCaptureTimer.Start();
        }

        private void AutoCapture()
        {
            if (string.IsNullOrEmpty(_saveFolderPath)) return;

            if (_frame != null && !_frame.Empty())
            {
                Mat frameCopy;
                lock (_frameLock)
                {
                    frameCopy = _frame.Clone();
                }

                // 使用锁读取 enhancer 参数并处理
                Mat enhancedFrame;
                lock (_enhancerLock)
                {
                    enhancedFrame = _enhancer.Enhance(frameCopy);
                }

                string filename = $"auto_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string fullPath = Path.Combine(_saveFolderPath, filename);

                try
                {
                    Cv2.ImWrite(fullPath, enhancedFrame);
                    Console.WriteLine("自动截图已保存: " + fullPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("保存失败: " + ex.Message);
                }
                finally
                {
                    enhancedFrame.Dispose();
                    frameCopy.Dispose();
                }
            }
        }

        // 切换显示面板
        private void BtnAutoCaptureMenu_Click(object sender, RoutedEventArgs e)
        {
            if (AutoCapturePanel.Visibility == Visibility.Collapsed)
                AutoCapturePanel.Visibility = Visibility.Visible;
            else
                AutoCapturePanel.Visibility = Visibility.Collapsed;
        }

        // 点击“开始自动截图”
        private void BtnStartAutoCapture_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_saveFolderPath))
            {
                System.Windows.MessageBox.Show("请先选择截图保存路径！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtCaptureInterval.Text, out int seconds) || seconds <= 0)
            {
                System.Windows.MessageBox.Show("请输入有效的截图间隔（正整数秒）！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (_autoCaptureTimer == null)
            {
                _autoCaptureTimer = new DispatcherTimer();
                _autoCaptureTimer.Tick += (s, ev) => AutoCapture();
            }

            _autoCaptureTimer.Interval = TimeSpan.FromSeconds(seconds);
            _autoCaptureTimer.Start();

            System.Windows.MessageBox.Show($"自动截图已开始（间隔 {seconds} 秒）", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // 点击“结束自动截图”
        private void BtnStopAutoCapture_Click(object sender, RoutedEventArgs e)
        {
            if (_autoCaptureTimer != null && _autoCaptureTimer.IsEnabled)
            {
                _autoCaptureTimer.Stop();
                System.Windows.MessageBox.Show("自动截图已停止", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show("当前未开启自动截图", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SelectPathButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择截图保存文件夹";
                dialog.ShowNewFolderButton = true;

                var result = dialog.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK)
                {
                    _saveFolderPath = dialog.SelectedPath;
                    SavePathBox.Text = _saveFolderPath;
                }
            }
        }

        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_saveFolderPath))
            {
                System.Windows.MessageBox.Show("请先选择保存路径！", "提示", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            if (_frame != null && !_frame.Empty())
            {
                // 新增：对截图进行增强
                Mat enhancedFrame = _enhancer.Enhance(_frame);

                string filename = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string fullPath = Path.Combine(_saveFolderPath, filename);

                try
                {
                    Cv2.ImWrite(fullPath, enhancedFrame);  // 保存增强后的帧
                    System.Windows.MessageBox.Show("截图已保存:\n" + fullPath, "成功", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("保存失败: " + ex.Message, "错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
                finally
                {
                    enhancedFrame.Dispose(); // 释放资源
                }
            }
        }
        // 关闭窗口时自动断开串口 断开摄像头
        protected override void OnClosed(EventArgs e)
        {
            _isRunning = false;
            _cameraThread?.Join();
            _capture?.Release();
            _frame?.Dispose();
            if (serialPort != null && serialPort.IsOpen)
                serialPort.Close();
            base.OnClosed(e);
        }

        // 刷新串口列表
        private void RefreshPorts()
        {
            cmbPorts.Items.Clear();
            var ports = SerialPort.GetPortNames();
            Array.Sort(ports);

            foreach (var port in ports)
                cmbPorts.Items.Add(port);

            if (cmbPorts.Items.Count > 0)
                cmbPorts.SelectedIndex = 0;
        }

        private void refreshCamera(object sender, RoutedEventArgs e)
        {
            StartCamera();
        }

        // “刷新”按钮点击事件
        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshPorts();
        }

        // 新增：刷新摄像头按钮点击事件
        private void BtnRefreshCamera_Click(object sender, RoutedEventArgs e)
        {
            RefreshCamera();
        }

        // 新增：刷新摄像头方法
        private void RefreshCamera()
        {
            _isRunning = false;
            _cameraThread?.Join();
            _autoCaptureTimer?.Stop();  // <—— 停掉旧的定时器
            _capture?.Release();
            _frame?.Dispose();
            StartCamera(); // 重新启动
        }
        private void UpdateEnhancerParams()
        {
            if (SliderAlpha == null || SliderBeta == null || SliderClipLimit == null || ComboTileGridSize == null || SliderGamma == null)
                return; // 或者抛出更友好的提示

            _enhancer.Alpha = SliderAlpha.Value;
            _enhancer.Beta = (int)SliderBeta.Value;
            _enhancer.ClipLimit = SliderClipLimit.Value;

            switch (ComboTileGridSize.SelectedIndex)
            {
                case 0: _enhancer.TileGridSize = new OpenCvSharp.Size(8, 8); break;
                case 1: _enhancer.TileGridSize = new OpenCvSharp.Size(4, 4); break;
                case 2: _enhancer.TileGridSize = new OpenCvSharp.Size(16, 16); break;
            }

            _enhancer.Gamma = SliderGamma.Value;
        }

        private void EnhancerParamsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateEnhancerParams();
        }
        private void ComboTileGridSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            EnhancerParamsChanged(sender, null); // 如果EnhancerParamsChanged需要参数，可以适当调整
        }

        public class ImageEnhancer
        {
            // 可调整的增强参数（根据需求修改）
            public double Alpha { get; set; } = 1.5;       // 全局对比度因子（1.0-2.0为宜） 默认1.5
            public int Beta { get; set; } = -5;            // 全局亮度偏移（-50到50为宜） 默认0
            public double ClipLimit { get; set; } = 2.0;  // CLAHE对比度限制（1.0-3.0为宜） 默认2.0
            public OpenCvSharp.Size TileGridSize { get; set; } = new OpenCvSharp.Size(8, 8);  // CLAHE网格大小 默认8*8
            public double Gamma { get; set; } = 1.2;      // 伽马矫正系数（0.8-1.5为宜） 默认1.2

            /// <summary>
            /// 对摄像头帧进行增强处理
            /// </summary>
            /// <param name="frame">原始BGR帧</param>
            /// <returns>增强后的BGR帧</returns>
            public Mat Enhance(Mat frame)
            {
                if (frame.Empty())
                    return frame;

                // 1. 转换为YCrCb色彩空间（分离亮度和色彩）
                Mat ycrcb = new Mat();
                Cv2.CvtColor(frame, ycrcb, ColorConversionCodes.BGR2YCrCb);

                // 2. 分离通道（Y=亮度，Cr/Cb=色彩）
                Mat[] channels = Cv2.Split(ycrcb);
                Mat yChannel = channels[0];  // 亮度通道
                Mat crChannel = channels[1];
                Mat cbChannel = channels[2];

                // 3. 全局对比度/亮度调整（仅作用于亮度通道）
                Mat yAdjusted = new Mat();
                Cv2.ConvertScaleAbs(yChannel, yAdjusted, Alpha, Beta);

                // 4. CLAHE局部对比度增强（仅作用于亮度通道）
                Mat yEnhanced = new Mat();
                using (var clahe = Cv2.CreateCLAHE(ClipLimit, TileGridSize))
                {
                    clahe.Apply(yAdjusted, yEnhanced);
                }

                // 5. 合并通道并转回BGR色彩空间（恢复色彩）
                Mat enhancedYcrcb = new Mat();
                Cv2.Merge(new[] { yEnhanced, crChannel, cbChannel }, enhancedYcrcb);
                Mat enhancedBgr = new Mat();
                Cv2.CvtColor(enhancedYcrcb, enhancedBgr, ColorConversionCodes.YCrCb2BGR);

                // 6. 伽马矫正（优化明暗细节）
                Mat result = GammaCorrection(enhancedBgr, Gamma);

                // 释放中间变量（避免内存泄漏）
                ycrcb.Dispose();
                foreach (var ch in channels) ch.Dispose();
                yAdjusted.Dispose();
                yEnhanced.Dispose();
                enhancedYcrcb.Dispose();

                return result;
            }

            /// <summary>
            /// 伽马矫正实现
            /// </summary>
            private Mat GammaCorrection(Mat image, double gamma)
            {
                if (gamma <= 0)
                    return image.Clone();

                // 创建伽马矫正查找表（8位单通道，1行256列）
                Mat lut = new Mat(1, 256, MatType.CV_8UC1);

                // 直接获取Mat的字节指针并赋值（避免使用InputArray）
                unsafe
                {
                    byte* ptr = (byte*)lut.DataPointer;  // 获取Mat的内存指针
                    double invGamma = 1.0 / gamma;
                    for (int i = 0; i < 256; i++)
                    {
                        ptr[i] = (byte)Math.Min(255, Math.Max(0, Math.Pow(i / 255.0, invGamma) * 255));
                    }
                }

                // 应用查找表
                Mat result = new Mat();
                Cv2.LUT(image, lut, result);
                lut.Dispose();
                return result;
            }
        }

        // “连接”按钮点击事件
        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (serialPort != null && serialPort.IsOpen)
            {
                System.Windows.MessageBox.Show("串口已连接。");
                return;
            }

            if (cmbPorts.SelectedItem == null)
            {
                System.Windows.MessageBox.Show("请先选择一个串口！");
                return;
            }

            try
            {
                serialPort = new SerialPort(cmbPorts.SelectedItem.ToString(), 115200); // 默认波特率9600
                serialPort.Open();
                System.Windows.MessageBox.Show("串口连接成功！");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("连接失败: " + ex.Message);
            }
            serialPort.DataReceived += SerialPort_DataReceived;
        }

        // “断开”按钮点击事件
        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (serialPort != null && serialPort.IsOpen)
            {
                try
                {
                    serialPort.Close();
                    System.Windows.MessageBox.Show("串口已断开。");
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("断开失败: " + ex.Message);
                }
            }
            else
            {
                System.Windows.MessageBox.Show("串口尚未连接。");
            }
        }
        private void Motor_Forward(object sender, RoutedEventArgs e)
        {
            if (serialPort == null || !serialPort.IsOpen)
            {
                System.Windows.MessageBox.Show("串口未连接！");
                return;
            }

            string numberText = editableComboBox.Text.Trim();

            if (!int.TryParse(numberText, out int steps))
            {
                System.Windows.MessageBox.Show("请输入合法数字！");
                return;
            }

            try
            {
                // 1. 发送使能命令 'V\n'
                serialPort.WriteLine("V");

                // 2. 等待 Arduino 处理 V（可选）
                Thread.Sleep(100); // 可视情况调整

                // 3. 发送数字步数（如 "200\n"）
                serialPort.WriteLine(numberText);

                //System.Windows.MessageBox.Show($"已发送 V + {numberText}");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("发送失败：" + ex.Message);
            }
        }

        private void Motor_Backwards(object sender, RoutedEventArgs e)
        {
            if (serialPort == null || !serialPort.IsOpen)
            {
                System.Windows.MessageBox.Show("串口未连接！");
                return;
            }

            string numberText = editableComboBox.Text.Trim();

            if (!int.TryParse(numberText, out int steps))
            {
                System.Windows.MessageBox.Show("请输入合法数字！");
                return;
            }

            // 发送前强制加负号
            steps = -Math.Abs(steps);  // 无论用户输正负数，最终都变成负数
            string sendValue = steps.ToString();


            try
            {
                // 1. 发送使能命令 'V\n'
                serialPort.WriteLine("V");

                // 2. 等待 Arduino 处理 V（可选）
                Thread.Sleep(100); // 可视情况调整

                // 3. 发送数字步数（如 "200\n"）
                serialPort.WriteLine(sendValue);

                //System.Windows.MessageBox.Show($"已发送 V + {numberText}");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("发送失败：" + ex.Message);
            }
        }

        private void Motor_Stop(object sender, RoutedEventArgs e)
        {
            try
            {
                // 1. 发送
                serialPort.WriteLine("C");

                System.Windows.MessageBox.Show("电机停止使能");
            } 
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("发送失败：" + ex.Message);
            }
        }

        private void LightOn(object sender, RoutedEventArgs e)
        {
            if (serialPort == null || !serialPort.IsOpen)
            {
                System.Windows.MessageBox.Show("串口未连接！");
                return;
            }
            try
            {
                // 发送使能命令 'L\n'
                serialPort.WriteLine("Z");
                System.Windows.MessageBox.Show("灯光已开启");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("发送失败：" + ex.Message);
            }
        }

        private void LightOff(object sender, RoutedEventArgs e)
        {
            if (serialPort == null || !serialPort.IsOpen)
            {
                System.Windows.MessageBox.Show("串口未连接！");
                return;
            }
            try
            {
                // 发送使能命令 'L\n'
                serialPort.WriteLine("X");
                System.Windows.MessageBox.Show("灯光已关闭");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("发送失败：" + ex.Message);
            }
        }
        private void Motor_Home(object sender, RoutedEventArgs e)
        {
            if (serialPort == null || !serialPort.IsOpen)
            {
                System.Windows.MessageBox.Show("串口未连接！");
                return;
            }

            isHoming = true;
            // 发送使能命令
            serialPort.WriteLine("V");
            Thread homingThread = new Thread(() =>
            {
                while (isHoming)
                {
                    // 持续发送负数步进
                    serialPort.WriteLine("-5000"); // 每次移动100步，可根据实际调整
                    Thread.Sleep(3000); // 等待电机响应
                }
            });
            homingThread.IsBackground = true;
            homingThread.Start();
        }

        // 解析串口数据并更新图表
        // 串口数据接收
        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            string line = serialPort.ReadLine();
            // 日志输出到调试窗口
            System.Diagnostics.Debug.WriteLine("串口收到: " + line);
            var now = DateTime.Now;
            // 归位信号判断
            if (line.Contains("ReverseLimitTriggered"))
            {
                isHoming = false;
                Dispatcher.Invoke(() =>
                {
                    System.Windows.MessageBox.Show("电机已归位！");
                });
                return;
            }
            // 匹配 co2（支持小数点，忽略大小写）
            var co2Match = System.Text.RegularExpressions.Regex.Match(line, @"co2[:：]?\s*([\d\.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (co2Match.Success)
            {
                double co2 = double.Parse(co2Match.Groups[1].Value);
                Dispatcher.Invoke(() =>
                {
                    Co2Values.Add(new DateTimePoint(now, co2));
                    if (Co2Values.Count > 50) Co2Values.RemoveAt(0);
                });
            }
            // 匹配温度（支持小数点）
            var tempMatch = System.Text.RegularExpressions.Regex.Match(line, @"temp[:：]?\s*([\d\.]+)");
            if (tempMatch.Success)
            {
                double temp = double.Parse(tempMatch.Groups[1].Value);
                Dispatcher.Invoke(() =>
                {
                    TempValues.Add(new DateTimePoint(now, temp));
                    if (TempValues.Count > 50) TempValues.RemoveAt(0);
                });
            }
        }
    }
}
