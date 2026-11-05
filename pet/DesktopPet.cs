// DSH Desktop Pet — Windows WPF shell.
//
// This file is compiled at runtime by DesktopPet.ps1 through Add-Type, so it
// targets the C# 5 compiler that ships with Windows PowerShell 5.1.  Keep the
// syntax conservative (no string interpolation, no null-conditional operator,
// no tuples, no expression-bodied members).
//
// The process is a separate OS window.  It has no Node or Electron privileges;
// its only link to the DSH host is the authenticated loopback TCP line protocol
// implemented by PetBridge.  User files are only ever handed to the Windows
// Shell recycle API (Microsoft.VisualBasic.FileIO.FileSystem with
// RecycleOption.SendToRecycleBin); this file contains no permanent-delete call.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Line = System.Windows.Shapes.Line;
using Ellipse = System.Windows.Shapes.Ellipse;
using WinForms = System.Windows.Forms;
using WPoint = System.Windows.Point;
using WRect = System.Windows.Rect;
using WColor = System.Windows.Media.Color;
using WBrushes = System.Windows.Media.Brushes;
using DPoint = System.Drawing.Point;
using DRect = System.Drawing.Rectangle;

namespace DshDesktopPet
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string configPath = null;
            int port = 0;
            string token = "";
            bool demo = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "-ConfigPath" && i + 1 < args.Length) configPath = args[++i];
                else if (a == "-Port" && i + 1 < args.Length) int.TryParse(args[++i], out port);
                else if (a == "-Token" && i + 1 < args.Length) token = args[++i];
                else if (a == "-Demo") demo = true;
            }

            Native.TryEnablePerMonitorDpi();
            Log.Init();
            Log.Info("pet starting; config=" + (configPath == null ? "(none)" : configPath)
                + " port=" + port.ToString(CultureInfo.InvariantCulture) + " demo=" + demo.ToString());

            PetConfig config = PetConfig.Load(configPath);
            PetBridge bridge = null;
            try
            {
                if (!demo && port > 0 && token.Length > 0)
                {
                    bridge = new PetBridge(port, token);
                    bridge.Start();
                }

                System.Windows.Application app = new System.Windows.Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                {
                    Log.Error("dispatcher exception: " + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    Log.Error("unhandled exception: " + Convert.ToString(e.ExceptionObject, CultureInfo.InvariantCulture));
                };
                TaskScheduler.UnobservedTaskException += delegate(object sender, UnobservedTaskExceptionEventArgs e)
                {
                    Log.Error("unobserved task exception: " + e.Exception);
                    e.SetObserved();
                };
                PetWindow window = new PetWindow(config, bridge, demo);
                app.MainWindow = window;
                app.Run(window);
                Log.Info("application loop exited");
                return 0;
            }
            catch (Exception error)
            {
                Log.Error("fatal: " + error);
                try
                {
                    System.Windows.MessageBox.Show(
                        "桌宠启动失败：\n" + error.Message + "\n\n详细日志：" + Log.FilePath,
                        "DSH Desktop Pet",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch
                {
                    // A headless desktop may not permit a dialog; the log remains.
                }
                return 1;
            }
            finally
            {
                if (bridge != null) bridge.Dispose();
            }
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string path;

        public static string DataDir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("DSH_PET_DATA_DIR");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DshDesktopPet");
            }
        }

        public static string FilePath
        {
            get
            {
                if (path == null)
                {
                    string dir = DataDir;
                    Directory.CreateDirectory(dir);
                    path = Path.Combine(dir, "pet.log");
                }
                return path;
            }
        }

        public static void Init()
        {
            try
            {
                string p = FilePath;
                if (File.Exists(p) && new FileInfo(p).Length > 2 * 1024 * 1024)
                {
                    // Keep every generation: no permanent-delete call exists in
                    // this project, even for the pet's own diagnostic log.
                    string old = p + ".old." + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                    File.Move(p, old);
                }
            }
            catch
            {
                // Logging must never prevent the pet from starting.
            }
        }

        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }
        public static void Error(string message) { Write("ERROR", message); }

        private static void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + " [" + level + "] " + message + Environment.NewLine;
            lock (Gate)
            {
                try
                {
                    File.AppendAllText(FilePath, line, new UTF8Encoding(false));
                }
                catch
                {
                    // Ignore a broken log destination.
                }
            }
            Debug.WriteLine(line.TrimEnd());
        }
    }

    internal static class Json
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static string Serialize(object value)
        {
            return Serializer.Serialize(value);
        }

        public static Dictionary<string, object> Parse(string text)
        {
            try
            {
                object value = Serializer.DeserializeObject(text);
                Dictionary<string, object> dict = value as Dictionary<string, object>;
                return dict;
            }
            catch (Exception error)
            {
                Log.Warn("json parse failed: " + error.Message);
                return null;
            }
        }

        public static Dictionary<string, object> Dict(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static List<object> List(object value)
        {
            List<object> list = value as List<object>;
            if (list != null) return list;
            object[] array = value as object[];
            if (array != null) return new List<object>(array);
            ArrayList legacy = value as ArrayList;
            if (legacy != null) return new List<object>(legacy.Cast<object>());
            return null;
        }

        public static string Str(Dictionary<string, object> dict, string key, string fallback)
        {
            if (dict == null || !dict.ContainsKey(key)) return fallback;
            object value = dict[key];
            return value == null ? fallback : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static int Int(Dictionary<string, object> dict, string key, int fallback)
        {
            if (dict == null || !dict.ContainsKey(key)) return fallback;
            try { return Convert.ToInt32(dict[key], CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static double Double(Dictionary<string, object> dict, string key, double fallback)
        {
            if (dict == null || !dict.ContainsKey(key)) return fallback;
            try { return Convert.ToDouble(dict[key], CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static bool Bool(Dictionary<string, object> dict, string key, bool fallback)
        {
            if (dict == null || !dict.ContainsKey(key)) return fallback;
            try { return Convert.ToBoolean(dict[key], CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }
    }

    internal sealed class InteractionDef
    {
        public string Id = "line";
        public double Weight = 1.0;
        public string Animation = "line";
        public string Sprite = "";
        public List<string> Texts = new List<string>();
    }

    internal sealed class PetConfig
    {
        public string ConfigPath = "";
        public string SpriteDir = "";
        public string SoundDir = "";
        public string ImagePath = "";
        public double Scale = 1.0;
        public double Volume = 0.7;
        public bool Topmost = true;
        public bool Shadow = true;
        public bool AvoidFullscreen = true;
        public bool Debug = false;
        public int LongPressMs = 450;
        public int DragStartPx = 10;
        public int EdgeThreshold = 24;
        public double EdgeVisibleRatio = 0.38;
        public int EdgeHideDelayMs = 750;
        public int EdgeSlideMs = 180;
        public int BubbleMs = 3800;
        public string StatsDisplay = "always";
        public string SpriteIdle = "idle";
        public string SpriteWork = "think";
        public string SpriteComplete = "heart";
        public string SpriteError = "sleepy";
        public string SpriteDrag = "peace";
        public string SpriteEat = "open";
        public string SpriteChew = "chew";
        public string SpriteShy = "shy";
        public string SpriteCheer = "cheer";
        public List<string> ProtectPaths = new List<string>();
        public List<InteractionDef> Interactions = new List<InteractionDef>();
        public Dictionary<string, string> ToolLabels = new Dictionary<string, string>();
        public Dictionary<string, string> SpriteFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> Lines = new List<string>();

        public static PetConfig Load(string path)
        {
            PetConfig config = new PetConfig();
            config.ConfigPath = path == null ? "" : path;
            Dictionary<string, object> root = null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { root = Json.Parse(File.ReadAllText(path, Encoding.UTF8)); }
                catch (Exception error) { Log.Error("config read failed: " + error); }
            }
            if (root == null) root = new Dictionary<string, object>();

            config.SpriteDir = Json.Str(root, "spriteDir", "");
            config.SoundDir = Json.Str(root, "soundDir", "");
            config.ImagePath = Json.Str(root, "imagePath", "");
            config.Scale = Clamp(Json.Double(root, "scale", 1.0), 0.5, 2.0);
            config.Volume = Clamp(Json.Double(root, "volume", 0.7), 0.0, 1.0);
            config.Topmost = Json.Bool(root, "topmost", true);
            config.Shadow = Json.Bool(root, "shadow", true);
            config.AvoidFullscreen = Json.Bool(root, "avoidFullscreen", true);
            config.Debug = Json.Bool(root, "debug", false);
            config.LongPressMs = Clamp(Json.Int(root, "longPressMs", 450), 120, 3000);
            config.DragStartPx = Clamp(Json.Int(root, "dragStartPx", 10), 2, 40);
            config.EdgeThreshold = Clamp(Json.Int(root, "edgeThreshold", 24), 4, 160);
            config.EdgeVisibleRatio = Clamp(Json.Double(root, "edgeVisibleRatio", 0.38), 0.12, 0.85);
            config.EdgeHideDelayMs = Clamp(Json.Int(root, "edgeHideDelayMs", 750), 100, 10000);
            config.EdgeSlideMs = Clamp(Json.Int(root, "edgeSlideMs", 180), 40, 1000);
            config.BubbleMs = Clamp(Json.Int(root, "bubbleMs", 3800), 800, 20000);
            config.StatsDisplay = Json.Str(root, "statsDisplay", "always");

            Dictionary<string, object> sprites = Json.Dict(root.ContainsKey("sprites") ? root["sprites"] : null);
            if (sprites != null)
            {
                foreach (KeyValuePair<string, object> pair in sprites)
                {
                    string relative = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(relative)) config.SpriteFiles[pair.Key] = relative;
                }
            }
            config.SpriteIdle = Json.Str(root, "spriteIdle", "idle");
            config.SpriteWork = Json.Str(root, "spriteWork", "think");
            config.SpriteComplete = Json.Str(root, "spriteComplete", "heart");
            config.SpriteError = Json.Str(root, "spriteError", "sleepy");
            config.SpriteDrag = Json.Str(root, "spriteDrag", "peace");
            config.SpriteEat = Json.Str(root, "spriteEat", "open");
            config.SpriteChew = Json.Str(root, "spriteChew", "chew");
            config.SpriteShy = Json.Str(root, "spriteShy", "shy");
            config.SpriteCheer = Json.Str(root, "spriteCheer", "cheer");

            List<object> protect = Json.List(root.ContainsKey("protectPaths") ? root["protectPaths"] : null);
            if (protect != null)
            {
                foreach (object item in protect)
                {
                    string value = Convert.ToString(item, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(value)) config.ProtectPaths.Add(value);
                }
            }
            if (config.ProtectPaths.Count == 0)
            {
                config.ProtectPaths.Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                config.ProtectPaths.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
                config.ProtectPaths.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            }

            List<object> interactions = Json.List(root.ContainsKey("interactions") ? root["interactions"] : null);
            if (interactions != null)
            {
                foreach (object item in interactions)
                {
                    Dictionary<string, object> entry = Json.Dict(item);
                    if (entry == null) continue;
                    InteractionDef def = new InteractionDef();
                    def.Id = Json.Str(entry, "id", "line");
                    def.Weight = Clamp(Json.Double(entry, "weight", 1.0), 0.0, 100.0);
                    def.Animation = Json.Str(entry, "animation", def.Id);
                    def.Sprite = Json.Str(entry, "sprite", "");
                    List<object> texts = Json.List(entry.ContainsKey("texts") ? entry["texts"] : null);
                    if (texts != null)
                    {
                        foreach (object t in texts)
                        {
                            string text = Convert.ToString(t, CultureInfo.InvariantCulture);
                            if (!string.IsNullOrEmpty(text)) def.Texts.Add(text);
                        }
                    }
                    if (def.Weight > 0) config.Interactions.Add(def);
                }
            }
            if (config.Interactions.Count == 0) config.Interactions = DefaultInteractions();

            Dictionary<string, object> toolLabels = Json.Dict(root.ContainsKey("toolLabels") ? root["toolLabels"] : null);
            if (toolLabels != null)
            {
                foreach (KeyValuePair<string, object> pair in toolLabels)
                {
                    config.ToolLabels[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                }
            }
            if (config.ToolLabels.Count == 0) config.ToolLabels = DefaultToolLabels();

            List<object> lines = Json.List(root.ContainsKey("lines") ? root["lines"] : null);
            if (lines != null)
            {
                foreach (object item in lines)
                {
                    string value = Convert.ToString(item, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(value)) config.Lines.Add(value);
                }
            }
            if (config.Lines.Count == 0)
            {
                config.Lines.Add("戳我干嘛呀～");
                config.Lines.Add("今天也要加油！");
                config.Lines.Add("要摸摸头吗？");
                config.Lines.Add("我可是很厉害的！");
                config.Lines.Add("再戳就咬你哦（骗你的）");
                config.Lines.Add("记得喝水呀～");
            }
            return config;
        }

        public static List<InteractionDef> DefaultInteractions()
        {
            List<InteractionDef> list = new List<InteractionDef>();
            list.Add(New("blink", 1.2, "blink", "sleepy", "眨眼~", "看不到我看不到我"));
            list.Add(New("jump", 1.0, "jump", "cheer", "跳一下！", "嘿咻！"));
            list.Add(New("shake", 1.0, "shake", "shy", "晃晃脑袋～", "晕乎乎…"));
            list.Add(New("heart", 0.8, "heart", "heart", "给你一颗小心心", "最喜欢你啦"));
            list.Add(New("line", 1.4, "line", "shy", "今天也要加油！", "要摸摸头吗？", "再戳就咬你哦（骗你的）", "记得喝水呀～"));
            list.Add(New("dance", 0.6, "dance", "cheer", "跳舞给你看！", "啦啦啦～"));
            return list;
        }

        private static InteractionDef New(string id, double weight, string animation, string sprite, params string[] texts)
        {
            InteractionDef def = new InteractionDef();
            def.Id = id;
            def.Weight = weight;
            def.Animation = animation;
            def.Sprite = sprite;
            def.Texts.AddRange(texts);
            return def;
        }

        public static Dictionary<string, string> DefaultToolLabels()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            map["pwsh"] = "运行命令";
            map["bash"] = "运行命令";
            map["read"] = "看文件";
            map["write"] = "写文件";
            map["edit"] = "改代码";
            map["fs"] = "整理文件";
            map["web_search"] = "上网查资料";
            map["web_fetch"] = "打开网页";
            map["todo_write"] = "列计划";
            map["subagent"] = "叫小伙伴帮忙";
            map["workflow"] = "编排任务";
            map["present"] = "生成成果";
            return map;
        }

        public static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        public static double Clamp(double value, double min, double max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }

    public sealed class RecycleResult
    {
        public int Moved;
        public List<string> Failures = new List<string>();
        public bool Ok { get { return Failures.Count == 0; } }
    }

    public static class RecycleService
    {
        // IFileOperation with FOFX_RECYCLEONDELETE is the only delete path in
        // this project.  That flag tells the Shell to recycle the item and to
        // FAIL the operation when recycling is not possible (Recycle Bin
        // disabled, item too large, network share, permissions).  There is no
        // fallback to File.Delete / Directory.Delete / SHFileOperation without
        // the flag, so a failure keeps the original in place.
        private const uint FOF_SILENT = 0x00000004;
        private const uint FOF_NOCONFIRMATION = 0x00000010;
        private const uint FOF_ALLOWUNDO = 0x00000040;
        private const uint FOF_NOERRORUI = 0x00000400;
        private const uint FOF_NO_CONNECTED_ELEMENTS = 0x00002000;
        private const uint FOFX_RECYCLEONDELETE = 0x00080000;
        private const uint RecycleFlags =
            FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI |
            FOF_NO_CONNECTED_ELEMENTS | FOFX_RECYCLEONDELETE;

        private static readonly Guid CLSID_FileOperation = new Guid("3AD05575-8857-4850-9277-11B85BDB8E09");

        public static string RestrictedReason()
        {
            // A confined process (AppContainer or low-integrity token) may have
            // its Shell recycle request silently turned into a permanent
            // delete by the host policy.  Refuse instead of risking data loss.
            try
            {
                IntPtr token;
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return "";
                try
                {
                    int isAppContainer;
                    int returned;
                    if (GetTokenInformationInt(token, TOKEN_IS_APP_CONTAINER, out isAppContainer, 4, out returned) &&
                        isAppContainer != 0)
                    {
                        return "AppContainer";
                    }
                    int size;
                    GetTokenInformationPtr(token, TOKEN_INTEGRITY_LEVEL, IntPtr.Zero, 0, out size);
                    if (size > 0)
                    {
                        IntPtr buffer = Marshal.AllocHGlobal(size);
                        try
                        {
                            if (GetTokenInformationPtr(token, TOKEN_INTEGRITY_LEVEL, buffer, size, out returned))
                            {
                                IntPtr sid = Marshal.ReadIntPtr(buffer);
                                IntPtr countPtr = GetSidSubAuthorityCount(sid);
                                byte count = Marshal.ReadByte(countPtr);
                                if (count > 0)
                                {
                                    IntPtr ridPtr = GetSidSubAuthority(sid, (uint)(count - 1));
                                    int rid = Marshal.ReadInt32(ridPtr);
                                    if (rid < 0x2000) return "低完整性级别";
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(buffer);
                        }
                    }
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            catch
            {
                // If the token cannot be inspected, the normal Shell path still
                // uses FOFX_RECYCLEONDELETE and fails rather than deleting.
            }
            return "";
        }

        public static RecycleResult Recycle(IEnumerable<string> paths, IEnumerable<string> protectedPaths)
        {
            RecycleResult result = new RecycleResult();
            string restricted = RestrictedReason();
            if (restricted.Length > 0)
            {
                result.Failures.Add("桌宠运行在受限进程（" + restricted
                    + "）中，为避免永久删除，已拒绝回收；原文件未做任何改动。请在普通 DSH 桌面进程中重试。");
                return result;
            }
            List<string> protect = new List<string>();
            if (protectedPaths != null)
            {
                foreach (string p in protectedPaths)
                {
                    if (!string.IsNullOrEmpty(p)) protect.Add(Normalize(p));
                }
            }

            List<string> incoming = new List<string>();
            if (paths != null)
            {
                foreach (string p in paths)
                {
                    if (!string.IsNullOrEmpty(p)) incoming.Add(p);
                }
            }
            if (incoming.Count == 0) return result;

            foreach (string raw in incoming)
            {
                string full;
                try { full = Path.GetFullPath(raw); }
                catch (Exception error)
                {
                    result.Failures.Add(raw + "：路径无效（" + error.Message + "）");
                    continue;
                }

                if (IsProtected(full, protect))
                {
                    result.Failures.Add(full + "：属于系统保护目录，未做任何改动");
                    continue;
                }

                try
                {
                    bool isFile = File.Exists(full);
                    bool isDirectory = Directory.Exists(full);
                    if (!isFile && !isDirectory)
                    {
                        result.Failures.Add(full + "：不存在或已被移动");
                        continue;
                    }
                    FileAttributes attributes = File.GetAttributes(full);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        result.Failures.Add(full + "：是符号链接/目录联接，为安全起见未处理");
                        continue;
                    }

                    RecycleOne(full);
                    if (File.Exists(full) || Directory.Exists(full))
                    {
                        result.Failures.Add(full + "：系统没有把它移入回收站，原文件已保留");
                    }
                    else
                    {
                        result.Moved++;
                    }
                }
                catch (OperationCanceledException)
                {
                    result.Failures.Add(full + "：操作已取消，原文件已保留");
                }
                catch (UnauthorizedAccessException)
                {
                    result.Failures.Add(full + "：没有权限移入回收站，原文件已保留");
                }
                catch (IOException error)
                {
                    result.Failures.Add(full + "：文件被占用或磁盘错误（" + error.Message + "），原文件已保留");
                }
                catch (COMException error)
                {
                    result.Failures.Add(full + "：系统拒绝移入回收站（0x"
                        + error.HResult.ToString("X8", CultureInfo.InvariantCulture)
                        + "），原文件已保留");
                }
                catch (Exception error)
                {
                    result.Failures.Add(full + "：回收失败（" + error.Message + "），原文件已保留");
                }
            }
            return result;
        }

        private static void RecycleOne(string full)
        {
            IShellItem item = null;
            IFileOperation operation = null;
            try
            {
                Guid shellItemId = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(full, IntPtr.Zero, ref shellItemId, out item);
                object instance = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_FileOperation));
                operation = (IFileOperation)instance;
                Throw(operation.SetOperationFlags(RecycleFlags));
                Throw(operation.DeleteItem(item, IntPtr.Zero));
                Throw(operation.PerformOperations());
                int aborted;
                Throw(operation.GetAnyOperationsAborted(out aborted));
                if (aborted != 0) throw new OperationCanceledException("Shell aborted the recycle operation");
            }
            finally
            {
                Release(item);
                Release(operation);
            }
        }

        private static void Throw(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        private static void Release(object value)
        {
            try
            {
                if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
            }
            catch
            {
                // A finalizer will release an already-disconnected RCW.
            }
        }

        private const uint TOKEN_QUERY = 0x0008;
        private const int TOKEN_INTEGRITY_LEVEL = 25;
        private const int TOKEN_IS_APP_CONTAINER = 29;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")]
        private static extern bool GetTokenInformationInt(IntPtr token, int informationClass, out int information, int length, out int returnedLength);

        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")]
        private static extern bool GetTokenInformationPtr(IntPtr token, int informationClass, IntPtr information, int length, out int returnedLength);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem ppsi);
            [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport]
        [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation
        {
            [PreserveSig] int Advise(IntPtr pfops, out uint pdwCookie);
            [PreserveSig] int Unadvise(uint dwCookie);
            [PreserveSig] int SetOperationFlags(uint dwOperationFlags);
            [PreserveSig] int SetProgressMessage(string pszMessage);
            [PreserveSig] int SetProgressDialog(IntPtr popd);
            [PreserveSig] int SetProperties(IntPtr pproparray);
            [PreserveSig] int SetOwnerWindow(IntPtr hwndOwner);
            [PreserveSig] int ApplyPropertiesToItem(IShellItem psiItem);
            [PreserveSig] int ApplyPropertiesToItems(IntPtr punkItems);
            [PreserveSig] int RenameItem(IShellItem psiItem, string pszNewName, IntPtr pfopsItem);
            [PreserveSig] int RenameItems(IntPtr pUnkItems, string pszNewName);
            [PreserveSig] int MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, IntPtr pfopsItem);
            [PreserveSig] int MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
            [PreserveSig] int CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, string pszCopyName, IntPtr pfopsItem);
            [PreserveSig] int CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
            [PreserveSig] int DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
            [PreserveSig] int DeleteItems(IntPtr punkItems);
            [PreserveSig] int NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, string pszName, string pszTemplateName, IntPtr pfopsItem);
            [PreserveSig] int PerformOperations();
            [PreserveSig] int GetAnyOperationsAborted(out int pfAnyOperationsAborted);
        }

        private static bool IsProtected(string full, List<string> protect)
        {
            string driveRoot;
            try { driveRoot = Path.GetPathRoot(full); }
            catch { driveRoot = null; }
            if (!string.IsNullOrEmpty(driveRoot) &&
                string.Equals(full.TrimEnd('\\'), driveRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            string normalized = Normalize(full);
            foreach (string prefix in protect)
            {
                if (normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                if (normalized.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\'); }
            catch { return path.TrimEnd('\\'); }
        }
    }

    internal sealed class SoundPool : IDisposable
    {
        private readonly string dir;
        private readonly double volume;
        private readonly List<MediaPlayer> active = new List<MediaPlayer>();
        private bool fallbackWarned;
        private bool warnedMissing;

        public SoundPool(string directory, double volume)
        {
            dir = directory;
            this.volume = PetConfig.Clamp(volume, 0.0, 1.0);
        }

        public void Play(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            string file = Path.Combine(dir, name + ".wav");
            if (!File.Exists(file))
            {
                if (!warnedMissing)
                {
                    warnedMissing = true;
                    Log.Warn("sound missing: " + file);
                }
                Fallback(file);
                return;
            }
            try
            {
                MediaPlayer player = new MediaPlayer();
                player.Volume = volume;
                player.MediaOpened += delegate
                {
                    try { player.Play(); }
                    catch (Exception error) { Log.Warn("audio play failed: " + error.Message); Cleanup(player); Fallback(file); }
                };
                player.MediaEnded += delegate { Cleanup(player); };
                player.MediaFailed += delegate(object sender, ExceptionEventArgs error)
                {
                    Log.Warn("audio failed: " + error.ErrorException.Message);
                    Cleanup(player);
                    Fallback(file);
                };
                lock (active) active.Add(player);
                player.Open(new Uri(file, UriKind.Absolute));
            }
            catch (Exception error)
            {
                Log.Warn("audio open failed: " + error.Message);
                Fallback(file);
            }
        }

        private void Cleanup(MediaPlayer player)
        {
            lock (active) active.Remove(player);
            try
            {
                player.Stop();
                player.Close();
            }
            catch
            {
                // A disconnected media graph is already inert.
            }
        }

        private void Fallback(string file)
        {
            // A WAV fallback keeps the completion cue even when the media graph
            // is unavailable; it has no per-sound volume, so it is only used
            // after a real failure.
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    using (System.Media.SoundPlayer fallback = new System.Media.SoundPlayer(file))
                    {
                        fallback.Load();
                        fallback.PlaySync();
                    }
                }
                catch (Exception error)
                {
                    Log.Warn("audio fallback failed: " + error.Message);
                    if (fallbackWarned) return;
                    fallbackWarned = true;
                    try { System.Media.SystemSounds.Asterisk.Play(); }
                    catch { }
                }
            });
        }

        public void Dispose()
        {
            MediaPlayer[] players;
            lock (active)
            {
                players = active.ToArray();
                active.Clear();
            }
            foreach (MediaPlayer player in players)
            {
                try { player.Stop(); player.Close(); } catch { }
            }
        }
    }

    internal sealed class SpriteEntry
    {
        public BitmapSource Image;
        public byte[] Alpha;
        public int PixelWidth;
        public int PixelHeight;
    }

    internal sealed class SpriteSet
    {
        private readonly Dictionary<string, SpriteEntry> entries =
            new Dictionary<string, SpriteEntry>(StringComparer.OrdinalIgnoreCase);
        public SpriteEntry Fallback;

        public static SpriteSet Load(PetConfig config)
        {
            SpriteSet set = new SpriteSet();
            string baseDir = config.SpriteDir;
            if (string.IsNullOrEmpty(baseDir) && !string.IsNullOrEmpty(config.ImagePath))
            {
                baseDir = Path.GetDirectoryName(config.ImagePath);
            }
            Log.Info("sprite directory=" + (baseDir ?? "(null)") + " exists=" + (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir)).ToString());
            if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
            {
                string[] files = Directory.GetFiles(baseDir, "*.png");
                Log.Info("sprite png count=" + files.Length.ToString(CultureInfo.InvariantCulture));
                foreach (string file in files)
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name == "contact-sheet") continue;
                    SpriteEntry entry = LoadEntry(file);
                    if (entry != null) set.entries[name] = entry;
                }
            }
            if (!string.IsNullOrEmpty(config.ImagePath) && File.Exists(config.ImagePath))
            {
                SpriteEntry single = LoadEntry(config.ImagePath);
                if (single != null)
                {
                    foreach (string name in new string[] { "idle", "think", "heart", "sleepy", "peace", "open", "chew", "shy", "cheer", "wave", "walk", "hold" })
                    {
                        if (!set.entries.ContainsKey(name)) set.entries[name] = single;
                    }
                }
            }
            foreach (KeyValuePair<string, string> pair in config.SpriteFiles)
            {
                string path = pair.Value;
                if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(baseDir)) path = Path.Combine(baseDir, pair.Value);
                SpriteEntry entry = LoadEntry(path);
                if (entry != null) set.entries[pair.Key] = entry;
            }
            if (!set.entries.ContainsKey("idle"))
            {
                SpriteEntry fallback = CreateFallback();
                set.entries["idle"] = fallback;
                set.Fallback = fallback;
                Log.Warn("no idle sprite loaded; using built-in fallback");
            }
            else
            {
                set.Fallback = set.entries["idle"];
            }
            return set;
        }

        public SpriteEntry Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return Fallback;
            SpriteEntry entry;
            if (entries.TryGetValue(name, out entry)) return entry;
            if (entries.TryGetValue("idle", out entry)) return entry;
            return Fallback;
        }

        private static SpriteEntry LoadEntry(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return FromBitmap(image);
            }
            catch (Exception error)
            {
                Log.Warn("sprite load failed: " + path + " (" + error.Message + ")");
                return null;
            }
        }

        private static SpriteEntry FromBitmap(BitmapSource source)
        {
            BitmapSource bitmap = source;
            if (bitmap.Format != PixelFormats.Bgra32)
            {
                FormatConvertedBitmap converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                bitmap = converted;
            }
            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);
            byte[] alpha = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    alpha[y * width + x] = pixels[y * stride + x * 4 + 3];
                }
            }
            SpriteEntry entry = new SpriteEntry();
            entry.Image = bitmap;
            entry.Alpha = alpha;
            entry.PixelWidth = width;
            entry.PixelHeight = height;
            return entry;
        }

        private static SpriteEntry CreateFallback()
        {
            int width = 240;
            int height = 260;
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                LinearGradientBrush body = new LinearGradientBrush(
                    WColor.FromRgb(255, 214, 84), WColor.FromRgb(241, 164, 18), 90);
                dc.DrawEllipse(body, null, new WPoint(120, 150), 92, 104);
                dc.DrawEllipse(WBrushes.White, null, new WPoint(88, 120), 22, 26);
                dc.DrawEllipse(WBrushes.White, null, new WPoint(152, 120), 22, 26);
                dc.DrawEllipse(WBrushes.Black, null, new WPoint(92, 124), 12, 15);
                dc.DrawEllipse(WBrushes.Black, null, new WPoint(148, 124), 12, 15);
                dc.DrawEllipse(new SolidColorBrush(WColor.FromRgb(120, 78, 20)), null, new WPoint(120, 168), 18, 11);
            }
            RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return FromBitmap(bitmap);
        }
    }

    internal sealed class PetBridge : IDisposable
    {
        private readonly int port;
        private readonly string token;
        private readonly object sendGate = new object();
        private TcpClient client;
        private StreamReader reader;
        private StreamWriter writer;
        private Thread thread;
        private volatile bool running;
        private bool disposed;

        public event Action<Dictionary<string, object>> MessageReceived;
        public event Action<string> Disconnected;

        public PetBridge(int port, string token)
        {
            this.port = port;
            this.token = token;
        }

        public void Start()
        {
            running = true;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "dsh-pet-bridge";
            thread.Start();
        }

        private void Run()
        {
            string reason = "disconnected";
            try
            {
                client = new TcpClient();
                client.NoDelay = true;
                client.Connect("127.0.0.1", port);
                NetworkStream stream = client.GetStream();
                reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096);
                writer = new StreamWriter(stream, new UTF8Encoding(false), 4096);
                writer.AutoFlush = false;
                Send(new Dictionary<string, object> {
                    { "type", "hello" },
                    { "token", token },
                    { "pid", Process.GetCurrentProcess().Id }
                });
                while (running)
                {
                    string line = reader.ReadLine();
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    Dictionary<string, object> message = Json.Parse(line);
                    if (message == null) continue;
                    string type = Json.Str(message, "type", "");
                    if (type == "pong") continue;
                    Action<Dictionary<string, object>> handler = MessageReceived;
                    if (handler != null) handler(message);
                }
                reason = "host closed the connection";
            }
            catch (Exception error)
            {
                reason = error.Message;
                Log.Warn("bridge error: " + error.Message);
            }
            finally
            {
                running = false;
                try { if (client != null) client.Close(); } catch { }
                Action<string> handler = Disconnected;
                if (!disposed && handler != null) handler(reason);
            }
        }

        public void Send(Dictionary<string, object> message)
        {
            if (message == null) return;
            lock (sendGate)
            {
                if (writer == null) return;
                try
                {
                    writer.WriteLine(Json.Serialize(message));
                    writer.Flush();
                }
                catch (Exception error)
                {
                    Log.Warn("bridge send failed: " + error.Message);
                }
            }
        }

        public void Dispose()
        {
            disposed = true;
            running = false;
            try { if (client != null) client.Close(); } catch { }
            try { if (thread != null && thread.IsAlive) thread.Join(500); } catch { }
        }
    }

    internal sealed class PetMotion
    {
        public double Angle;
        public double AngleVelocity;
        public double AngleTarget;
        public double Scale = 1.0;
        public double ScaleVelocity;
        public double ScaleTarget = 1.0;
        public double SwayX;
        public double SwayVelocity;
        public double SwayTarget;
        public double JumpY;
        public double JumpVelocity;
        public bool Jumping;

        public void Jump()
        {
            Jumping = true;
            JumpVelocity = -430.0;
        }

        public void Impulse(double velocity)
        {
            AngleVelocity += velocity;
        }

        public void Step(double dt)
        {
            // Damped spring: stable for the 16 ms frame tick and pleasant for
            // the pendulum-like hanging motion.
            double stiffness = 110.0;
            double damping = 13.0;
            AngleVelocity += (stiffness * (AngleTarget - Angle) - damping * AngleVelocity) * dt;
            Angle += AngleVelocity * dt;
            ScaleVelocity += (stiffness * (ScaleTarget - Scale) - damping * ScaleVelocity) * dt;
            Scale += ScaleVelocity * dt;
            SwayVelocity += (stiffness * (SwayTarget - SwayX) - damping * SwayVelocity) * dt;
            SwayX += SwayVelocity * dt;

            if (Jumping)
            {
                JumpVelocity += 1700.0 * dt;
                JumpY += JumpVelocity * dt;
                if (JumpY >= 0.0 && JumpVelocity > 0.0)
                {
                    JumpY = 0.0;
                    JumpVelocity = 0.0;
                    Jumping = false;
                }
            }
            Angle = PetConfig.Clamp(Angle, -32.0, 32.0);
            Scale = PetConfig.Clamp(Scale, 0.78, 1.22);
            SwayX = PetConfig.Clamp(SwayX, -22.0, 22.0);
        }
    }

    internal static class Native
    {
        public const int WM_NCHITTEST = 0x0084;
        public const int HTTRANSPARENT = -1;
        public const int HTCLIENT = 1;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int state);

        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        public static void TryEnablePerMonitorDpi()
        {
            try
            {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
                if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware();
            }
            catch
            {
                try { SetProcessDPIAware(); } catch { }
            }
        }

        public static DRect GetVirtualScreen()
        {
            return new DRect(
                GetSystemMetrics(SM_XVIRTUALSCREEN),
                GetSystemMetrics(SM_YVIRTUALSCREEN),
                GetSystemMetrics(SM_CXVIRTUALSCREEN),
                GetSystemMetrics(SM_CYVIRTUALSCREEN));
        }

        public static DPoint CursorPosition()
        {
            POINT p;
            GetCursorPos(out p);
            return new DPoint(p.X, p.Y);
        }

        public static bool TryGetWindowRect(IntPtr hwnd, out DRect rect)
        {
            RECT r;
            rect = new DRect();
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out r)) return false;
            rect = new DRect(r.Left, r.Top, r.Width, r.Height);
            return true;
        }

        public static void MoveWindow(IntPtr hwnd, int x, int y)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public static void ReassertTopmost(IntPtr hwnd)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    internal sealed class PetWindow : Window
    {
        private readonly PetConfig config;
        private readonly PetBridge bridge;
        private readonly bool demo;
        private readonly double uiScale;
        private readonly SpriteSet sprites;
        private readonly SoundPool sounds;
        private readonly Random random = new Random();
        private readonly PetMotion motion = new PetMotion();

        private Canvas root;
        private Image petImage;
        private ScaleTransform petScale;
        private RotateTransform petRotate;
        private TranslateTransform petTranslate;
        private Border bubble;
        private TextBlock bubbleText;
        private Border progress;
        private TextBlock progressLabel;
        private ProgressBar progressBar;
        private Border stats;
        private TextBlock statsText;
        private Line rope;
        private Ellipse ropeHook;

        private DispatcherTimer frameTimer;
        private DispatcherTimer edgeTimer;
        private DispatcherTimer fullscreenTimer;
        private DispatcherTimer longPressTimer;
        private DispatcherTimer bubbleTimer;
        private DispatcherTimer chewTimer;
        private DispatcherTimer demoTimer;
        private DispatcherTimer progressHideTimer;
        private DispatcherTimer bridgeLossTimer;

        private IntPtr hwnd;
        private DRect windowRect;
        private int desiredLeft = int.MinValue;
        private int desiredTop = int.MinValue;
        private bool closing;
        private bool readySent;
        private bool mouseOver;
        private bool pressed;
        private bool dragging;
        private bool pickedUp;
        private bool fileDrag;
        private bool fullscreenHidden;
        private DateTime pressStarted = DateTime.UtcNow;
        private WPoint pressPoint;
        private DPoint pressScreen;
        private DPoint dragStartScreen;
        private DRect dragStartRect;
        private DPoint lastDragScreen;
        private DateTime lastDragTick = DateTime.UtcNow;
        private double dragVelocity;
        private DateTime lastBubble = DateTime.MinValue;
        private DateTime lastStatsAt = DateTime.MinValue;
        private DateTime hiddenSince = DateTime.MinValue;
        private string hiddenEdge = "";
        private DRect shownRect;
        private DRect hiddenRect;
        private string currentSprite = "";
        private string statsLine = "今日 —";
        private string balanceLine = "余额 —";
        private string statsDetail = "";
        private DateTime lastChew = DateTime.MinValue;
        private int chewStep;
        private int danceStep;
        private int demoStep;

        public PetWindow(PetConfig config, PetBridge bridge, bool demo)
        {
            this.config = config;
            this.bridge = bridge;
            this.demo = demo;
            this.uiScale = PetConfig.Clamp(config.Scale, 0.5, 2.0);
            this.sprites = SpriteSet.Load(config);
            this.sounds = new SoundPool(config.SoundDir, config.Volume);

            Title = "DSH Desktop Pet";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = WBrushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Topmost = config.Topmost;
            ShowActivated = false;
            Width = 360 * uiScale;
            Height = 430 * uiScale;
            WindowStartupLocation = WindowStartupLocation.Manual;
            // Position is applied in physical pixels after the HWND exists;
            // WPF's Left/Top are DIPs and would double-apply the monitor DPI.
            Left = 0;
            Top = 0;
            Opacity = 1;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            BuildUi();
            LoadWindowPosition();
            StartTimers();

            Loaded += OnLoaded;
            SourceInitialized += OnSourceInitialized;
            Closing += OnClosing;
            MouseEnter += OnMouseEnter;
            MouseLeave += OnMouseLeave;

            if (bridge != null)
            {
                bridge.MessageReceived += OnBridgeMessage;
                bridge.Disconnected += OnBridgeDisconnected;
            }
        }

        private double S(double value)
        {
            return value * uiScale;
        }

        private double F(double value)
        {
            // Keep tiny UI legible even at scale 0.5; the pet body still halves.
            return Math.Max(9.0, value * uiScale);
        }

        private void BuildUi()
        {
            root = new Canvas();
            root.Background = null;
            Content = root;

            petImage = new Image();
            petImage.Width = S(260);
            petImage.Height = S(260);
            petImage.Stretch = Stretch.Uniform;
            petImage.IsHitTestVisible = false;
            RenderOptions.SetBitmapScalingMode(petImage, BitmapScalingMode.HighQuality);
            petScale = new ScaleTransform(1, 1);
            petRotate = new RotateTransform(0);
            petTranslate = new TranslateTransform(0, 0);
            TransformGroup group = new TransformGroup();
            group.Children.Add(petScale);
            group.Children.Add(petRotate);
            group.Children.Add(petTranslate);
            petImage.RenderTransform = group;
            petImage.RenderTransformOrigin = new WPoint(0.5, 0.9);
            Canvas.SetLeft(petImage, S(50));
            Canvas.SetTop(petImage, S(132));
            root.Children.Add(petImage);

            if (config.Shadow)
            {
                DropShadowEffect shadow = new DropShadowEffect();
                shadow.BlurRadius = S(18);
                shadow.ShadowDepth = S(7);
                shadow.Opacity = 0.35;
                shadow.Color = WColor.FromRgb(0, 0, 0);
                petImage.Effect = shadow;
            }

            rope = new Line();
            rope.Stroke = new SolidColorBrush(WColor.FromRgb(255, 206, 92));
            rope.StrokeThickness = S(2.5);
            rope.StrokeDashArray = new DoubleCollection(new double[] { S(4), S(3) });
            rope.Visibility = Visibility.Collapsed;
            rope.IsHitTestVisible = false;
            root.Children.Add(rope);

            ropeHook = new Ellipse();
            ropeHook.Width = S(12);
            ropeHook.Height = S(12);
            ropeHook.Fill = new SolidColorBrush(WColor.FromRgb(255, 206, 92));
            ropeHook.Visibility = Visibility.Collapsed;
            ropeHook.IsHitTestVisible = false;
            root.Children.Add(ropeHook);

            bubble = new Border();
            bubble.Background = new SolidColorBrush(WColor.FromArgb(238, 30, 31, 42));
            bubble.CornerRadius = new CornerRadius(S(14));
            bubble.Padding = new Thickness(S(13), S(9), S(13), S(9));
            bubble.Visibility = Visibility.Collapsed;
            bubble.IsHitTestVisible = false;
            bubble.MaxWidth = S(330);
            DropShadowEffect bubbleShadow = new DropShadowEffect();
            bubbleShadow.BlurRadius = S(12);
            bubbleShadow.ShadowDepth = S(4);
            bubbleShadow.Opacity = 0.35;
            bubble.Effect = bubbleShadow;
            bubbleText = new TextBlock();
            bubbleText.Foreground = WBrushes.White;
            bubbleText.FontSize = F(13);
            bubbleText.FontFamily = new FontFamily("Microsoft YaHei UI");
            bubbleText.TextWrapping = TextWrapping.Wrap;
            bubbleText.TextAlignment = TextAlignment.Center;
            bubble.Child = bubbleText;
            root.Children.Add(bubble);

            progress = new Border();
            progress.Background = new SolidColorBrush(WColor.FromArgb(235, 30, 31, 42));
            progress.CornerRadius = new CornerRadius(S(12));
            progress.Padding = new Thickness(S(12), S(7), S(12), S(8));
            progress.Visibility = Visibility.Collapsed;
            progress.IsHitTestVisible = false;
            progress.Width = S(280);
            StackPanel progressStack = new StackPanel();
            progressLabel = new TextBlock();
            progressLabel.Foreground = WBrushes.White;
            progressLabel.FontSize = F(12);
            progressLabel.FontFamily = new FontFamily("Microsoft YaHei UI");
            progressLabel.TextTrimming = TextTrimming.CharacterEllipsis;
            progressStack.Children.Add(progressLabel);
            progressBar = new ProgressBar();
            progressBar.Height = S(5);
            progressBar.Margin = new Thickness(0, S(5), 0, 0);
            progressBar.IsIndeterminate = true;
            progressBar.Foreground = new SolidColorBrush(WColor.FromRgb(255, 194, 61));
            progressBar.Background = new SolidColorBrush(WColor.FromArgb(60, 255, 255, 255));
            progressBar.BorderThickness = new Thickness(0);
            progressStack.Children.Add(progressBar);
            progress.Child = progressStack;
            Canvas.SetLeft(progress, S(40));
            Canvas.SetTop(progress, S(98));
            root.Children.Add(progress);

            stats = new Border();
            stats.Background = new SolidColorBrush(WColor.FromArgb(218, 24, 25, 34));
            stats.CornerRadius = new CornerRadius(S(15));
            stats.Padding = new Thickness(S(12), S(6), S(12), S(6));
            stats.Cursor = Cursors.Hand;
            stats.Width = S(340);
            stats.Visibility = config.StatsDisplay == "off" ? Visibility.Collapsed : Visibility.Visible;
            statsText = new TextBlock();
            statsText.Foreground = new SolidColorBrush(WColor.FromRgb(255, 235, 190));
            statsText.FontSize = F(12);
            statsText.FontFamily = new FontFamily("Microsoft YaHei UI");
            statsText.TextAlignment = TextAlignment.Center;
            statsText.TextTrimming = TextTrimming.CharacterEllipsis;
            stats.Child = statsText;
            stats.MouseLeftButtonUp += delegate { RequestRefresh(); };
            stats.MouseEnter += delegate { mouseOver = true; if (hiddenEdge.Length > 0) ShowFromEdge(); };
            stats.MouseLeave += delegate { mouseOver = false; };
            Canvas.SetLeft(stats, S(10));
            Canvas.SetTop(stats, S(390));
            root.Children.Add(stats);

            ResetSprite();
            UpdateStatsDisplay();
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragOver += OnDragOver;
            DragLeave += OnDragLeave;
            Drop += OnDrop;
            MouseLeftButtonDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseUp;
            LostMouseCapture += OnLostCapture;
            MouseRightButtonUp += OnRightClick;
        }

        private void ResetSprite()
        {
            SetSprite(config.SpriteIdle);
        }

        private void SetSprite(string name)
        {
            SpriteEntry entry = sprites.Get(name);
            if (entry == null) entry = sprites.Fallback;
            if (entry == null) return;
            petImage.Source = entry.Image;
            currentSprite = name;
            if (entry.Image == sprites.Fallback.Image && name != "idle")
            {
                // A missing expression is not an error: keep the idle body and
                // continue, recording the fallback once.
                Log.Warn("sprite \"" + name + "\" missing; idle used");
            }
        }

        private SpriteEntry CurrentEntry()
        {
            return sprites.Get(currentSprite);
        }

        private void StartTimers()
        {
            frameTimer = new DispatcherTimer(DispatcherPriority.Render);
            frameTimer.Interval = TimeSpan.FromMilliseconds(16);
            frameTimer.Tick += OnFrame;
            frameTimer.Start();

            edgeTimer = new DispatcherTimer();
            edgeTimer.Interval = TimeSpan.FromMilliseconds(120);
            edgeTimer.Tick += OnEdgeTick;
            edgeTimer.Start();

            fullscreenTimer = new DispatcherTimer();
            fullscreenTimer.Interval = TimeSpan.FromMilliseconds(1500);
            fullscreenTimer.Tick += OnFullscreenTick;
            fullscreenTimer.Start();

            longPressTimer = new DispatcherTimer();
            longPressTimer.Interval = TimeSpan.FromMilliseconds(config.LongPressMs);
            longPressTimer.Tick += OnLongPress;
            longPressTimer.Stop();

            bubbleTimer = new DispatcherTimer();
            bubbleTimer.Interval = TimeSpan.FromMilliseconds(config.BubbleMs);
            bubbleTimer.Tick += OnBubbleTimeout;
            bubbleTimer.Stop();

            chewTimer = new DispatcherTimer();
            chewTimer.Interval = TimeSpan.FromMilliseconds(150);
            chewTimer.Tick += OnChewTick;
            chewTimer.Stop();

            progressHideTimer = new DispatcherTimer();
            progressHideTimer.Interval = TimeSpan.FromMilliseconds(1400);
            progressHideTimer.Tick += delegate { progressHideTimer.Stop(); HideProgress(); };
            progressHideTimer.Stop();

            bridgeLossTimer = new DispatcherTimer();
            bridgeLossTimer.Interval = TimeSpan.FromSeconds(6);
            bridgeLossTimer.Tick += delegate
            {
                bridgeLossTimer.Stop();
                if (!closing) Close();
            };
            bridgeLossTimer.Stop();

            if (demo)
            {
                demoTimer = new DispatcherTimer();
                demoTimer.Interval = TimeSpan.FromMilliseconds(3200);
                demoTimer.Tick += OnDemoTick;
                demoTimer.Start();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Log.Info("window loaded hwnd=" + hwnd.ToString());
                RestoreNativePosition();
                if (config.Topmost) Native.ReassertTopmost(hwnd);
                Log.Info("visual state opacity=" + Opacity.ToString("0.###", CultureInfo.InvariantCulture)
                    + " visible=" + IsVisible.ToString() + " topmost=" + Topmost.ToString()
                    + " actual=" + ActualWidth.ToString("0.#", CultureInfo.InvariantCulture) + "x" + ActualHeight.ToString("0.#", CultureInfo.InvariantCulture));
                if (demo)
                {
                    DispatcherTimer shot = new DispatcherTimer();
                    shot.Interval = TimeSpan.FromMilliseconds(1800);
                    shot.Tick += delegate
                    {
                        shot.Stop();
                        CaptureSelf();
                    };
                    shot.Start();
                }
                SendReady();
                ShowBubble(demo ? "演示模式：我是桌宠～" : "你好呀，我是桌宠～", 3200, "info");
                if (demo) UpdateDemoStats();
            }
            catch (Exception error)
            {
                Log.Error("OnLoaded failed: " + error);
                throw;
            }
        }

        private void CaptureSelf()
        {
            try
            {
                int width = Math.Max(1, (int)Math.Round(ActualWidth));
                int height = Math.Max(1, (int)Math.Round(ActualHeight));
                RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string path = Path.Combine(Log.DataDir, "self.png");
                using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    encoder.Save(stream);
                }
                Log.Info("self capture written: " + path + " " + width + "x" + height);
            }
            catch (Exception error)
            {
                Log.Warn("self capture failed: " + error.Message);
            }
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource source = HwndSource.FromHwnd(hwnd);
            if (source != null) source.AddHook(WndProc);
            // Position while the HWND exists but before the window is shown, so
            // the pet never flashes at the default WPF origin.
            RestoreNativePosition();
            if (config.Topmost) Native.ReassertTopmost(hwnd);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_NCHITTEST)
            {
                if (closing) return IntPtr.Zero;
                if (dragging || pickedUp)
                {
                    handled = true;
                    return new IntPtr(Native.HTCLIENT);
                }
                int x = (short)(lParam.ToInt64() & 0xFFFF);
                int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                WPoint screen = new WPoint(x, y);
                WPoint client = PointFromScreen(screen);
                if (!IsInteractive(client))
                {
                    handled = true;
                    return new IntPtr(Native.HTTRANSPARENT);
                }
            }
            return IntPtr.Zero;
        }

        private bool IsInteractive(WPoint point)
        {
            if (point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight) return false;
            if (stats.Visibility == Visibility.Visible && Contains(stats, point)) return true;
            if (progress.Visibility == Visibility.Visible && Contains(progress, point)) return true;
            if (bubble.Visibility == Visibility.Visible && Contains(bubble, point)) return true;

            WRect imageRect = new WRect(Canvas.GetLeft(petImage), Canvas.GetTop(petImage), petImage.ActualWidth, petImage.ActualHeight);
            if (!imageRect.Contains(point)) return false;
            SpriteEntry entry = CurrentEntry();
            if (entry == null || entry.Alpha == null) return true;
            double u = (point.X - imageRect.X) / Math.Max(1.0, imageRect.Width);
            double v = (point.Y - imageRect.Y) / Math.Max(1.0, imageRect.Height);
            int px = (int)(u * entry.PixelWidth);
            int py = (int)(v * entry.PixelHeight);
            if (px < 0 || py < 0 || px >= entry.PixelWidth || py >= entry.PixelHeight) return false;
            return entry.Alpha[py * entry.PixelWidth + px] > 24;
        }

        private static bool Contains(FrameworkElement element, WPoint point)
        {
            if (element == null || element.Visibility != Visibility.Visible) return false;
            WPoint local = element.TranslatePoint(new WPoint(0, 0), element.Parent as UIElement);
            WRect rect = new WRect(local.X, local.Y, element.ActualWidth, element.ActualHeight);
            return rect.Contains(point);
        }

        private void OnFrame(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            double dt = 0.016;
            motion.Step(dt);
            petScale.ScaleX = motion.Scale;
            petScale.ScaleY = motion.Scale;
            petRotate.Angle = motion.Angle;
            petTranslate.X = motion.SwayX;
            petTranslate.Y = motion.JumpY;

            if (dragging)
            {
                UpdateDrag();
                UpdateRope();
            }
            else if (pickedUp)
            {
                UpdateRope();
            }

            // Reassert topmost periodically: some shell surfaces and fullscreen
            // players demote a topmost window without telling it.
            if (!fullscreenHidden && config.Topmost && hwnd != IntPtr.Zero &&
                (now - lastDragTick).TotalSeconds > 2.0)
            {
                lastDragTick = now;
                Native.ReassertTopmost(hwnd);
            }
        }

        private void OnMouseEnter(object sender, MouseEventArgs e)
        {
            mouseOver = true;
            if (hiddenEdge.Length > 0) ShowFromEdge();
            if (stats.Visibility == Visibility.Visible && config.StatsDisplay == "hover") stats.Visibility = Visibility.Visible;
            UpdateStatsDisplay();
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            mouseOver = false;
            if (config.StatsDisplay == "hover") stats.Visibility = Visibility.Collapsed;
            if (hiddenEdge.Length > 0 && !dragging && !pickedUp) hiddenSince = DateTime.UtcNow;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (fileDrag) return;
            WPoint point = e.GetPosition(this);
            Log.Info("mouse down at " + point.X.ToString("0.#", CultureInfo.InvariantCulture) + ","
                + point.Y.ToString("0.#", CultureInfo.InvariantCulture) + " interactive=" + IsInteractive(point).ToString());
            if (!IsInteractive(point)) return;
            pressed = true;
            dragging = false;
            pickedUp = false;
            pressStarted = DateTime.UtcNow;
            pressPoint = point;
            pressScreen = Native.CursorPosition();
            motion.AngleTarget = 0;
            motion.ScaleTarget = 1.0;
            CaptureMouse();
            longPressTimer.Stop();
            longPressTimer.Start();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (pressed && !dragging && !pickedUp)
            {
                WPoint point = e.GetPosition(this);
                double dx = point.X - pressPoint.X;
                double dy = point.Y - pressPoint.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance > config.DragStartPx)
                {
                    longPressTimer.Stop();
                    BeginDrag(false);
                }
            }
            else if (pickedUp && !dragging)
            {
                WPoint point = e.GetPosition(this);
                double dx = point.X - pressPoint.X;
                double dy = point.Y - pressPoint.Y;
                if (Math.Sqrt(dx * dx + dy * dy) > 2.0)
                {
                    BeginDrag(true);
                }
            }
            if (!dragging && !pickedUp) return;
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            longPressTimer.Stop();
            if (dragging || pickedUp)
            {
                EndDrag();
            }
            else if (pressed)
            {
                double elapsed = (DateTime.UtcNow - pressStarted).TotalMilliseconds;
                WPoint point = e.GetPosition(this);
                double dx = point.X - pressPoint.X;
                double dy = point.Y - pressPoint.Y;
                if (elapsed < config.LongPressMs && Math.Sqrt(dx * dx + dy * dy) <= config.DragStartPx)
                {
                    RunRandomInteraction();
                }
            }
            pressed = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            e.Handled = true;
        }

        private void OnLostCapture(object sender, MouseEventArgs e)
        {
            if (closing) return;
            if (dragging || pickedUp)
            {
                EndDrag();
            }
            pressed = false;
            longPressTimer.Stop();
        }

        private void OnLongPress(object sender, EventArgs e)
        {
            longPressTimer.Stop();
            if (!pressed || dragging || pickedUp) return;
            WPoint point = Mouse.GetPosition(this);
            double dx = point.X - pressPoint.X;
            double dy = point.Y - pressPoint.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > config.DragStartPx) return;
            pickedUp = true;
            pressed = false;
            Log.Info("long press pickup");
            motion.ScaleTarget = 1.06;
            motion.AngleTarget = -7;
            petImage.RenderTransformOrigin = new WPoint(0.5, 0.08);
            rope.Visibility = Visibility.Visible;
            ropeHook.Visibility = Visibility.Visible;
            sounds.Play("pickup");
            ShowBubble("咦？被提起来了！", 1400, "info");
            CaptureMouse();
        }

        private void BeginDrag(bool fromPickup)
        {
            if (!pressed && !fromPickup) return;
            dragging = true;
            pressed = false;
            pickedUp = true;
            Log.Info("drag begin");
            dragStartScreen = Native.CursorPosition();
            lastDragScreen = dragStartScreen;
            DRect rect;
            if (Native.TryGetWindowRect(hwnd, out rect)) dragStartRect = rect;
            else dragStartRect = windowRect;
            lastDragTick = DateTime.UtcNow;
            dragVelocity = 0;
            longPressTimer.Stop();
            motion.ScaleTarget = 1.05;
            petImage.RenderTransformOrigin = new WPoint(0.5, 0.08);
            rope.Visibility = Visibility.Visible;
            ropeHook.Visibility = Visibility.Visible;
            SetSprite(config.SpriteDrag);
            ShowBubble("带我去哪儿？", 1200, "info");
            CaptureMouse();
        }

        private void UpdateDrag()
        {
            DPoint cursor = Native.CursorPosition();
            int dx = cursor.X - dragStartScreen.X;
            int dy = cursor.Y - dragStartScreen.Y;
            int x = dragStartRect.Left + dx;
            int y = dragStartRect.Top + dy;
            DRect virtualScreen = Native.GetVirtualScreen();
            x = Math.Max(virtualScreen.Left - dragStartRect.Width + 70, Math.Min(x, virtualScreen.Right - 70));
            y = Math.Max(virtualScreen.Top - dragStartRect.Height + 70, Math.Min(y, virtualScreen.Bottom - 70));
            Native.MoveWindow(hwnd, x, y);
            windowRect = new DRect(x, y, dragStartRect.Width, dragStartRect.Height);

            int vx = cursor.X - lastDragScreen.X;
            dragVelocity = dragVelocity * 0.7 + vx * 0.3;
            lastDragScreen = cursor;
            motion.AngleTarget = PetConfig.Clamp(-dragVelocity * 1.4, -24.0, 24.0);
            motion.SwayTarget = PetConfig.Clamp(dragVelocity * 0.35, -18.0, 18.0);
        }

        private void UpdateRope()
        {
            if (rope.Visibility != Visibility.Visible) return;
            DPoint cursor = Native.CursorPosition();
            WPoint p = PointFromScreen(new WPoint(cursor.X, cursor.Y));
            WRect petRect = new WRect(Canvas.GetLeft(petImage), Canvas.GetTop(petImage), petImage.ActualWidth, petImage.ActualHeight);
            double x2 = petRect.X + petRect.Width * 0.5;
            double y2 = petRect.Y + S(8);
            if (dragging && p.X > 0 && p.Y > 0 && p.X < ActualWidth && p.Y < ActualHeight)
            {
                rope.X1 = p.X;
                rope.Y1 = p.Y;
                Canvas.SetLeft(ropeHook, p.X - S(6));
                Canvas.SetTop(ropeHook, p.Y - S(6));
            }
            else
            {
                rope.X1 = x2;
                rope.Y1 = S(66);
                Canvas.SetLeft(ropeHook, x2 - S(6));
                Canvas.SetTop(ropeHook, S(60));
            }
            rope.X2 = x2;
            rope.Y2 = y2;
        }

        private void EndDrag()
        {
            dragging = false;
            pickedUp = false;
            Log.Info("drag end");
            motion.AngleTarget = 0;
            motion.AngleVelocity *= 0.8;
            motion.ScaleTarget = 1.0;
            motion.SwayTarget = 0;
            petImage.RenderTransformOrigin = new WPoint(0.5, 0.9);
            rope.Visibility = Visibility.Collapsed;
            ropeHook.Visibility = Visibility.Collapsed;
            SetSprite(config.SpriteIdle);
            Native.TryGetWindowRect(hwnd, out windowRect);
            SaveWindowPosition();
            sounds.Play("drop");
            ApplyEdgeSnap();
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        private void OnRightClick(object sender, MouseButtonEventArgs e)
        {
            WPoint point = e.GetPosition(this);
            if (!IsInteractive(point)) return;
            ContextMenu menu = BuildContextMenu();
            menu.PlacementTarget = this;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private ContextMenu BuildContextMenu()
        {
            ContextMenu menu = new ContextMenu();
            menu.Background = new SolidColorBrush(WColor.FromArgb(245, 28, 29, 38));
            menu.Foreground = WBrushes.White;
            menu.BorderBrush = new SolidColorBrush(WColor.FromArgb(80, 255, 255, 255));
            menu.FontFamily = new FontFamily("Microsoft YaHei UI");
            menu.FontSize = 12.5;

            MenuItem refresh = new MenuItem();
            refresh.Header = "刷新余额 / 花费";
            refresh.Click += delegate { RequestRefresh(); };
            menu.Items.Add(refresh);

            MenuItem stats = new MenuItem();
            stats.Header = "隐藏统计条";
            stats.Click += delegate
            {
                if (stats.Visibility == Visibility.Visible) stats.Visibility = Visibility.Collapsed;
                else stats.Visibility = Visibility.Visible;
            };
            menu.Items.Add(stats);

            MenuItem reset = new MenuItem();
            reset.Header = "回到屏幕右下角";
            reset.Click += delegate { MoveToDefaultPosition(); };
            menu.Items.Add(reset);

            menu.Items.Add(new Separator());

            MenuItem configItem = new MenuItem();
            configItem.Header = "打开配置说明";
            configItem.Click += delegate { SendRequest("open_config"); };
            menu.Items.Add(configItem);

            MenuItem restart = new MenuItem();
            restart.Header = "重启桌宠";
            restart.Click += delegate { SendRequest("restart"); };
            menu.Items.Add(restart);

            MenuItem quit = new MenuItem();
            quit.Header = "退出桌宠（本次会话）";
            quit.Click += delegate { SendRequest("quit"); Close(); };
            menu.Items.Add(quit);

            return menu;
        }

        private void RequestRefresh()
        {
            SendRequest("refresh_stats");
            ShowBubble("我去看看账单…", 1200, "info");
        }

        private void SendRequest(string action)
        {
            if (bridge == null) return;
            bridge.Send(new Dictionary<string, object> {
                { "type", "request" },
                { "action", action }
            });
        }

        private void SendReady()
        {
            if (bridge == null || readySent) return;
            readySent = true;
            bridge.Send(new Dictionary<string, object> {
                { "type", "ready" },
                { "version", "1.0.0" },
                { "pid", Process.GetCurrentProcess().Id }
            });
        }

        private void OnBridgeMessage(Dictionary<string, object> message)
        {
            if (message == null) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                try { HandleMessage(message); }
                catch (Exception error) { Log.Error("message handler failed: " + error); }
            }));
        }

        private void OnBridgeDisconnected(string reason)
        {
            if (closing) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                ShowBubble("和 Harness 断开啦，稍等…", 5000, "error");
                bridgeLossTimer.Start();
            }));
        }

        private void HandleMessage(Dictionary<string, object> message)
        {
            string type = Json.Str(message, "type", "");
            if (type == "stats") UpdateStats(message);
            else if (type == "agent") HandleAgent(message);
            else if (type == "bubble") ShowBubble(Json.Str(message, "text", ""), Json.Int(message, "durationMs", 3500), Json.Str(message, "tone", "info"));
            else if (type == "interaction") RunInteraction(Json.Str(message, "id", "line"));
            else if (type == "control") HandleControl(Json.Str(message, "action", ""));
            else if (type == "ping") bridge.Send(new Dictionary<string, object> { { "type", "pong" }, { "t", Json.Str(message, "t", "") } });
            else if (type == "shutdown") { closing = true; Close(); }
            else if (type == "init")
            {
                SendReady();
                Dictionary<string, object> statsMessage = Json.Dict(message.ContainsKey("stats") ? message["stats"] : null);
                if (statsMessage != null) UpdateStats(statsMessage);
            }
        }

        private void HandleControl(string action)
        {
            if (action == "show") { Show(); if (config.Topmost) Native.ReassertTopmost(hwnd); }
            else if (action == "hide") Hide();
            else if (action == "quit") { closing = true; Close(); }
            else if (action == "refresh") RequestRefresh();
            else if (action == "reload") ShowBubble("配置更新后我会重启哦", 2500, "info");
        }

        private void HandleAgent(Dictionary<string, object> message)
        {
            string ev = Json.Str(message, "event", "");
            string text = Json.Str(message, "text", "");
            string tool = Json.Str(message, "tool", "");
            int step = Json.Int(message, "step", 0);
            if (ev == "start")
            {
                SetSprite(config.SpriteWork);
                motion.ScaleTarget = 1.03;
                ShowProgress(text.Length > 0 ? text : "开始干活啦…", "", true);
                ShowBubble(text.Length > 0 ? text : "开始干活啦！", 2200, "info");
            }
            else if (ev == "progress")
            {
                SetSprite(config.SpriteWork);
                string label = text;
                if (label.Length == 0) label = step > 0 ? "第 " + step.ToString(CultureInfo.InvariantCulture) + " 步" : "正在努力…";
                if (tool.Length > 0)
                {
                    string friendly;
                    if (config.ToolLabels.TryGetValue(tool, out friendly)) label = label + " · " + friendly;
                    else label = label + " · " + tool;
                }
                ShowProgress(label, "", true);
                if ((DateTime.UtcNow - lastBubble).TotalMilliseconds > 1600)
                {
                    ShowBubble(label, 1400, "info");
                }
            }
            else if (ev == "complete")
            {
                SetSprite(config.SpriteComplete);
                motion.ScaleTarget = 1.04;
                motion.Jump();
                ShowBubble(text.Length > 0 ? text : "任务完成！", 5200, "done");
                progressHideTimer.Start();
                sounds.Play("complete");
                SpawnHearts(7);
            }
            else if (ev == "fail")
            {
                SetSprite(config.SpriteError);
                motion.ScaleTarget = 0.98;
                ShowBubble(text.Length > 0 ? text : "任务没成功…", 6000, "error");
                progressHideTimer.Start();
                sounds.Play("error");
            }
            else if (ev == "cancel")
            {
                SetSprite(config.SpriteIdle);
                motion.ScaleTarget = 1.0;
                ShowBubble(text.Length > 0 ? text : "任务已取消", 3200, "info");
                progressHideTimer.Start();
            }
            else if (ev == "celebrate")
            {
                SetSprite(config.SpriteComplete);
                motion.Jump();
                sounds.Play("complete");
                SpawnHearts(9);
                ShowBubble(text.Length > 0 ? text : "耶！", 3000, "done");
            }
        }

        private void ShowProgress(string label, string tool, bool indeterminate)
        {
            progressLabel.Text = label;
            progressBar.IsIndeterminate = indeterminate;
            progress.Visibility = Visibility.Visible;
            PositionBubble();
        }

        private void HideProgress()
        {
            progress.Visibility = Visibility.Collapsed;
            PositionBubble();
        }

        private void ShowBubble(string text, int durationMs, string tone)
        {
            if (string.IsNullOrEmpty(text)) return;
            lastBubble = DateTime.UtcNow;
            bubbleText.Text = text;
            if (tone == "done") bubble.Background = new SolidColorBrush(WColor.FromArgb(242, 42, 122, 82));
            else if (tone == "error") bubble.Background = new SolidColorBrush(WColor.FromArgb(242, 135, 42, 52));
            else if (tone == "eat") bubble.Background = new SolidColorBrush(WColor.FromArgb(242, 120, 76, 20));
            else bubble.Background = new SolidColorBrush(WColor.FromArgb(238, 30, 31, 42));
            bubble.Visibility = Visibility.Visible;
            bubble.Opacity = 1;
            PositionBubble();
            bubbleTimer.Stop();
            bubbleTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(700, durationMs));
            bubbleTimer.Start();
        }

        private void PositionBubble()
        {
            if (bubble.Visibility != Visibility.Visible) return;
            bubble.Measure(new Size(S(330), S(220)));
            Size size = bubble.DesiredSize;
            double left = (ActualWidth - size.Width) / 2.0;
            double petTop = Canvas.GetTop(petImage);
            double top = petTop - size.Height - S(10);
            if (progress.Visibility == Visibility.Visible) top = Math.Min(top, S(98) - size.Height - S(8));
            if (top < S(8)) top = Math.Min(petTop + petImage.ActualHeight + S(8), ActualHeight - size.Height - S(8));
            Canvas.SetLeft(bubble, Math.Max(S(8), left));
            Canvas.SetTop(bubble, Math.Max(S(8), top));
        }

        private void OnBubbleTimeout(object sender, EventArgs e)
        {
            bubbleTimer.Stop();
            DoubleAnimation fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
            fade.Completed += delegate
            {
                bubble.Visibility = Visibility.Collapsed;
                bubble.Opacity = 1;
            };
            bubble.BeginAnimation(OpacityProperty, fade);
        }

        private void UpdateStats(Dictionary<string, object> message)
        {
            statsLine = Json.Str(message, "line", "今日 —");
            balanceLine = Json.Str(message, "balance", "余额 —");
            statsDetail = Json.Str(message, "detail", "");
            bool stale = Json.Bool(message, "stale", false);
            if (stale) statsLine = "⏳ " + statsLine;
            string source = Json.Str(message, "source", "");
            if (source.Length > 0 && statsDetail.Length == 0) statsDetail = source;
            UpdateStatsDisplay();
            lastStatsAt = DateTime.UtcNow;
        }

        private void UpdateStatsDisplay()
        {
            if (statsText == null) return;
            string shown = statsLine + "  ·  " + balanceLine;
            statsText.Text = shown;
            stats.ToolTip = statsDetail.Length > 0 ? statsDetail : null;
            if (config.StatsDisplay == "off") stats.Visibility = Visibility.Collapsed;
            else if (config.StatsDisplay == "hover") stats.Visibility = mouseOver ? Visibility.Visible : Visibility.Collapsed;
            else stats.Visibility = Visibility.Visible;
        }

        private void SpawnHearts(int count)
        {
            for (int i = 0; i < count; i++)
            {
                TextBlock heart = new TextBlock();
                heart.Text = "♥";
                heart.FontSize = Math.Max(9.0, (14 + random.Next(10)) * uiScale);
                heart.Foreground = new SolidColorBrush(WColor.FromRgb((byte)(240 + random.Next(15)), (byte)(80 + random.Next(80)), (byte)(120 + random.Next(80))));
                heart.IsHitTestVisible = false;
                TranslateTransform transform = new TranslateTransform(0, 0);
                heart.RenderTransform = transform;
                double x = S(80 + random.Next(200));
                double y = S(150 + random.Next(100));
                Canvas.SetLeft(heart, x);
                Canvas.SetTop(heart, y);
                root.Children.Add(heart);
                DoubleAnimation rise = new DoubleAnimation(0, S(-90 - random.Next(50)), TimeSpan.FromMilliseconds(900 + random.Next(400)));
                rise.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                DoubleAnimation fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(900 + random.Next(400)));
                int delay = random.Next(180);
                rise.BeginTime = TimeSpan.FromMilliseconds(delay);
                fade.BeginTime = TimeSpan.FromMilliseconds(delay);
                fade.Completed += delegate { root.Children.Remove(heart); };
                transform.BeginAnimation(TranslateTransform.YProperty, rise);
                heart.BeginAnimation(OpacityProperty, fade);
            }
        }

        private void RunRandomInteraction()
        {
            Log.Info("click interaction requested");
            if (config.Interactions.Count == 0) return;
            double total = 0;
            foreach (InteractionDef def in config.Interactions) total += def.Weight;
            double pick = random.NextDouble() * total;
            InteractionDef selected = config.Interactions[0];
            foreach (InteractionDef def in config.Interactions)
            {
                pick -= def.Weight;
                if (pick <= 0) { selected = def; break; }
            }
            RunInteraction(selected.Id);
        }

        private void RunInteraction(string id)
        {
            InteractionDef def = null;
            foreach (InteractionDef item in config.Interactions)
            {
                if (string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) { def = item; break; }
            }
            if (def == null)
            {
                def = new InteractionDef();
                def.Id = id;
                def.Animation = id;
                def.Texts.AddRange(config.Lines);
            }
            string text = def.Texts.Count > 0 ? def.Texts[random.Next(def.Texts.Count)] : "";
            sounds.Play("click");
            string animation = string.IsNullOrEmpty(def.Animation) ? def.Id : def.Animation;
            // `sprite` on an interaction is authoritative when present.  The
            // per-animation state sprite is only the fallback, so a user can
            // add/remove click poses entirely from config.
            string clickSprite = string.IsNullOrEmpty(def.Sprite) ? null : def.Sprite;
            if (animation == "blink")
            {
                SetSprite(clickSprite ?? config.SpriteShy);
                motion.ScaleTarget = 0.97;
                DispatcherTimer restore = new DispatcherTimer();
                restore.Interval = TimeSpan.FromMilliseconds(160);
                restore.Tick += delegate
                {
                    restore.Stop();
                    SetSprite(config.SpriteIdle);
                    motion.ScaleTarget = 1.0;
                };
                restore.Start();
                if (text.Length > 0) ShowBubble(text, 1400, "info");
            }
            else if (animation == "jump")
            {
                motion.Jump();
                SetSprite(clickSprite ?? config.SpriteCheer);
                ShowBubble(text, 1600, "info");
                DispatcherTimer restore = new DispatcherTimer();
                restore.Interval = TimeSpan.FromMilliseconds(650);
                restore.Tick += delegate { restore.Stop(); SetSprite(config.SpriteIdle); motion.ScaleTarget = 1.0; };
                restore.Start();
            }
            else if (animation == "shake")
            {
                motion.Impulse(random.NextDouble() > 0.5 ? 260 : -260);
                SetSprite(clickSprite ?? config.SpriteShy);
                ShowBubble(text, 1500, "info");
                DispatcherTimer restore = new DispatcherTimer();
                restore.Interval = TimeSpan.FromMilliseconds(700);
                restore.Tick += delegate { restore.Stop(); SetSprite(config.SpriteIdle); };
                restore.Start();
            }
            else if (animation == "heart")
            {
                SetSprite(clickSprite ?? config.SpriteComplete);
                SpawnHearts(5);
                ShowBubble(text.Length > 0 ? text : "给你一颗小心心", 2200, "done");
                DispatcherTimer restore = new DispatcherTimer();
                restore.Interval = TimeSpan.FromMilliseconds(2200);
                restore.Tick += delegate { restore.Stop(); SetSprite(config.SpriteIdle); };
                restore.Start();
            }
            else if (animation == "dance")
            {
                SetSprite(clickSprite ?? config.SpriteCheer);
                motion.Impulse(random.NextDouble() > 0.5 ? 300 : -300);
                motion.ScaleTarget = 1.08;
                danceStep = 0;
                DispatcherTimer dancer = new DispatcherTimer();
                dancer.Interval = TimeSpan.FromMilliseconds(180);
                dancer.Tick += delegate
                {
                    danceStep++;
                    motion.Impulse(danceStep % 2 == 0 ? 180 : -180);
                    motion.ScaleTarget = danceStep % 2 == 0 ? 1.08 : 0.96;
                    if (danceStep >= 6)
                    {
                        dancer.Stop();
                        SetSprite(config.SpriteIdle);
                        motion.ScaleTarget = 1.0;
                    }
                };
                dancer.Start();
                ShowBubble(text.Length > 0 ? text : "跳舞给你看！", 2200, "info");
            }
            else
            {
                SetSprite(clickSprite ?? config.SpriteShy);
                if (text.Length > 0) ShowBubble(text, 2800, "info");
                motion.ScaleTarget = 1.03;
                DispatcherTimer restore = new DispatcherTimer();
                restore.Interval = TimeSpan.FromMilliseconds(2400);
                restore.Tick += delegate { restore.Stop(); SetSprite(config.SpriteIdle); motion.ScaleTarget = 1.0; };
                restore.Start();
            }
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            fileDrag = true;
            if (hiddenEdge.Length > 0) ShowFromEdge();
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            SetSprite(config.SpriteEat);
            motion.ScaleTarget = 1.07;
            ShowBubble("放进来，我吃掉它！", 5000, "eat");
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            fileDrag = false;
            SetSprite(config.SpriteIdle);
            motion.ScaleTarget = 1.0;
            ShowBubble("欸？不吃了吗…", 1800, "info");
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            fileDrag = false;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0)
            {
                SetSprite(config.SpriteIdle);
                motion.ScaleTarget = 1.0;
                return;
            }
            e.Handled = true;
            BeginEat(files);
        }

        private void BeginEat(string[] files)
        {
            SetSprite(config.SpriteEat);
            motion.ScaleTarget = 1.08;
            sounds.Play("eat");
            ShowBubble("啊呜——嚼嚼嚼…", 2400, "eat");
            chewStep = 0;
            lastChew = DateTime.UtcNow;
            chewTimer.Start();

            Task<RecycleResult> task = Task.Factory.StartNew(delegate
            {
                return RecycleService.Recycle(files, config.ProtectPaths);
            });
            task.ContinueWith(delegate(Task<RecycleResult> finished)
            {
                RecycleResult result;
                if (finished.IsFaulted)
                {
                    result = new RecycleResult();
                    result.Failures.Add("内部错误：" + (finished.Exception == null ? "未知" : finished.Exception.GetBaseException().Message));
                }
                else
                {
                    result = finished.Result;
                }
                Dispatcher.BeginInvoke(new Action(delegate { FinishEat(result); }));
            });
        }

        private void OnChewTick(object sender, EventArgs e)
        {
            chewStep++;
            if (chewStep % 2 == 0)
            {
                SetSprite(config.SpriteEat);
                motion.ScaleTarget = 1.06;
            }
            else
            {
                SetSprite(config.SpriteChew);
                motion.ScaleTarget = 0.95;
            }
            if (chewStep >= 7)
            {
                chewTimer.Stop();
                SetSprite(config.SpriteIdle);
                motion.ScaleTarget = 1.0;
            }
        }

        private void FinishEat(RecycleResult result)
        {
            chewTimer.Stop();
            if (result.Ok)
            {
                SetSprite(config.SpriteComplete);
                motion.ScaleTarget = 1.04;
                motion.Jump();
                SpawnHearts(5);
                sounds.Play("complete");
                ShowBubble("吃掉啦！已经送进回收站～", 5200, "done");
                DispatcherTimer restore = new DispatcherTimer();
                restore.Interval = TimeSpan.FromMilliseconds(2400);
                restore.Tick += delegate { restore.Stop(); SetSprite(config.SpriteIdle); motion.ScaleTarget = 1.0; };
                restore.Start();
            }
            else
            {
                SetSprite(config.SpriteError);
                motion.ScaleTarget = 0.98;
                sounds.Play("error");
                string summary = result.Moved > 0
                    ? "吃掉 " + result.Moved.ToString(CultureInfo.InvariantCulture) + " 个，另有 " + result.Failures.Count.ToString(CultureInfo.InvariantCulture) + " 个没吃成"
                    : "这个吃不下…原文件都还在";
                ShowBubble(summary, 6000, "error");
                ShowFailureWindow(result);
            }
            if (bridge != null)
            {
                bridge.Send(new Dictionary<string, object> {
                    { "type", "file_result" },
                    { "ok", result.Ok },
                    { "moved", result.Moved },
                    { "failures", result.Failures }
                });
            }
        }

        private void ShowFailureWindow(RecycleResult result)
        {
            Window dialog = new Window();
            dialog.Title = "桌宠没能吃掉这些文件";
            dialog.Width = 560;
            dialog.Height = 360;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.Owner = this;
            dialog.Topmost = true;
            dialog.Background = new SolidColorBrush(WColor.FromRgb(26, 27, 35));
            dialog.WindowStyle = WindowStyle.ToolWindow;
            dialog.ResizeMode = ResizeMode.CanResize;
            Grid grid = new Grid();
            grid.Margin = new Thickness(16);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBlock title = new TextBlock();
            title.Foreground = WBrushes.White;
            title.FontSize = 14;
            title.FontWeight = FontWeights.SemiBold;
            title.Text = "原文件已保留。只有成功移入回收站的文件才消失。";
            title.TextWrapping = TextWrapping.Wrap;
            grid.Children.Add(title);
            TextBox details = new TextBox();
            details.Margin = new Thickness(0, 10, 0, 10);
            details.IsReadOnly = true;
            details.TextWrapping = TextWrapping.Wrap;
            details.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            details.Background = new SolidColorBrush(WColor.FromRgb(18, 19, 26));
            details.Foreground = new SolidColorBrush(WColor.FromRgb(255, 226, 180));
            details.BorderThickness = new Thickness(0);
            details.FontFamily = new FontFamily("Consolas");
            details.Text = string.Join(Environment.NewLine, result.Failures.ToArray());
            Grid.SetRow(details, 1);
            grid.Children.Add(details);
            Button ok = new Button();
            ok.Content = "知道了";
            ok.Width = 96;
            ok.Height = 30;
            ok.HorizontalAlignment = HorizontalAlignment.Right;
            ok.Margin = new Thickness(0, 2, 0, 0);
            ok.Click += delegate { dialog.Close(); };
            Grid.SetRow(ok, 2);
            grid.Children.Add(ok);
            dialog.Content = grid;
            dialog.ShowDialog();
        }

        private void ApplyEdgeSnap()
        {
            if (hwnd == IntPtr.Zero) return;
            DRect rect;
            if (!Native.TryGetWindowRect(hwnd, out rect)) return;
            DRect area = ScreenAreaForRect(rect);
            int threshold = config.EdgeThreshold;
            int left = rect.Left - area.Left;
            int right = area.Right - rect.Right;
            int top = rect.Top - area.Top;
            int bottom = area.Bottom - rect.Bottom;
            int min = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
            if (min > threshold)
            {
                ClearEdge();
                return;
            }

            shownRect = rect;
            hiddenEdge = "";
            if (min == left)
            {
                hiddenEdge = "left";
                hiddenRect = new DRect(area.Left - (int)(rect.Width * (1.0 - config.EdgeVisibleRatio)), rect.Top, rect.Width, rect.Height);
            }
            else if (min == right)
            {
                hiddenEdge = "right";
                hiddenRect = new DRect(area.Right - (int)(rect.Width * config.EdgeVisibleRatio), rect.Top, rect.Width, rect.Height);
            }
            else if (min == top)
            {
                hiddenEdge = "top";
                hiddenRect = new DRect(rect.Left, area.Top - (int)(rect.Height * (1.0 - config.EdgeVisibleRatio)), rect.Width, rect.Height);
            }
            else
            {
                hiddenEdge = "bottom";
                hiddenRect = new DRect(rect.Left, area.Bottom - (int)(rect.Height * config.EdgeVisibleRatio), rect.Width, rect.Height);
            }
            hiddenSince = DateTime.UtcNow;
            Log.Info("edge snap " + hiddenEdge);
            // Keep it visible for a beat after the drop, then slide away.
            DispatcherTimer delay = new DispatcherTimer();
            delay.Interval = TimeSpan.FromMilliseconds(config.EdgeHideDelayMs);
            delay.Tick += delegate
            {
                delay.Stop();
                if (hiddenEdge.Length > 0 && !mouseOver && !dragging && !pickedUp) HideToEdge();
            };
            delay.Start();
        }

        private void ClearEdge()
        {
            hiddenEdge = "";
            shownRect = new DRect();
            hiddenRect = new DRect();
        }

        private void HideToEdge()
        {
            if (hiddenEdge.Length == 0) return;
            Log.Info("hide to edge " + hiddenEdge);
            SlideTo(hiddenRect, config.EdgeSlideMs);
        }

        private void ShowFromEdge()
        {
            if (hiddenEdge.Length == 0) return;
            Log.Info("show from edge " + hiddenEdge);
            SlideTo(shownRect, config.EdgeSlideMs);
            hiddenSince = DateTime.MinValue;
        }

        private void SlideTo(DRect target, int durationMs)
        {
            DRect start;
            if (!Native.TryGetWindowRect(hwnd, out start)) return;
            if (start.Left == target.Left && start.Top == target.Top) return;
            DateTime started = DateTime.UtcNow;
            DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Render);
            timer.Interval = TimeSpan.FromMilliseconds(15);
            timer.Tick += delegate
            {
                double elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
                double t = Math.Min(1.0, elapsed / Math.Max(1.0, durationMs));
                double eased = 1.0 - Math.Pow(1.0 - t, 3.0);
                int x = (int)(start.Left + (target.Left - start.Left) * eased);
                int y = (int)(start.Top + (target.Top - start.Top) * eased);
                Native.MoveWindow(hwnd, x, y);
                if (t >= 1.0)
                {
                    timer.Stop();
                    Native.MoveWindow(hwnd, target.Left, target.Top);
                    windowRect = target;
                }
            };
            timer.Start();
        }

        private void OnEdgeTick(object sender, EventArgs e)
        {
            if (closing || hwnd == IntPtr.Zero) return;
            if (dragging || pickedUp) return;
            if (hiddenEdge.Length == 0)
            {
                if (config.StatsDisplay == "hover") UpdateStatsDisplay();
                return;
            }
            DPoint cursor = Native.CursorPosition();
            DRect rect;
            if (!Native.TryGetWindowRect(hwnd, out rect)) return;
            bool inside = cursor.X >= rect.Left - 4 && cursor.X <= rect.Right + 4 &&
                          cursor.Y >= rect.Top - 4 && cursor.Y <= rect.Bottom + 4;
            if (inside)
            {
                mouseOver = true;
                ShowFromEdge();
            }
            else
            {
                mouseOver = false;
                if (hiddenSince == DateTime.MinValue) hiddenSince = DateTime.UtcNow;
                if ((DateTime.UtcNow - hiddenSince).TotalMilliseconds >= config.EdgeHideDelayMs)
                {
                    DRect current;
                    if (Native.TryGetWindowRect(hwnd, out current) &&
                        (current.Left != hiddenRect.Left || current.Top != hiddenRect.Top))
                    {
                        HideToEdge();
                    }
                }
            }
            if (config.StatsDisplay == "hover") UpdateStatsDisplay();
        }

        private void OnFullscreenTick(object sender, EventArgs e)
        {
            if (closing || hwnd == IntPtr.Zero || !config.AvoidFullscreen) return;
            if (dragging || pickedUp || mouseOver) return;
            bool fullscreen = IsForegroundFullscreen();
            if (fullscreen && !fullscreenHidden)
            {
                fullscreenHidden = true;
                Hide();
                Log.Info("hidden because a fullscreen app is in front");
            }
            else if (!fullscreen && fullscreenHidden)
            {
                fullscreenHidden = false;
                Show();
                if (config.Topmost) Native.ReassertTopmost(hwnd);
                Log.Info("restored after fullscreen app left");
            }
        }

        private bool IsForegroundFullscreen()
        {
            // QUNS_RUNNING_D3D_FULL_SCREEN / QUNS_BUSY / QUNS_PRESENTATION_MODE.
            // This is the shell's own notion of "do not disturb"; unlike a raw
            // rectangle comparison it does not mistake a maximized Electron
            // window that covers the taskbar for a fullscreen game.
            int state;
            if (Native.SHQueryUserNotificationState(out state) == 0)
            {
                return state == 2 || state == 3 || state == 4;
            }
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == hwnd) return false;
            if (foreground == Native.GetShellWindow()) return false;
            StringBuilder name = new StringBuilder(128);
            Native.GetClassName(foreground, name, name.Capacity);
            string cls = name.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            DRect rect;
            if (!Native.TryGetWindowRect(foreground, out rect)) return false;
            WinForms.Screen screen = ScreenFromRect(rect);
            DRect bounds = screen.Bounds;
            return Math.Abs(rect.Left - bounds.Left) <= 2 && Math.Abs(rect.Top - bounds.Top) <= 2 &&
                   Math.Abs(rect.Right - bounds.Right) <= 2 && Math.Abs(rect.Bottom - bounds.Bottom) <= 2;
        }

        private static DRect ScreenAreaForRect(DRect rect)
        {
            WinForms.Screen screen = ScreenFromRect(rect);
            return screen.WorkingArea;
        }

        private static WinForms.Screen ScreenFromRect(DRect rect)
        {
            WinForms.Screen best = null;
            long bestArea = -1;
            DRect center = new DRect(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2, 1, 1);
            foreach (WinForms.Screen screen in WinForms.Screen.AllScreens)
            {
                DRect area = screen.WorkingArea;
                DRect overlap = DRect.Intersect(rect, area);
                long size = (long)Math.Max(0, overlap.Width) * Math.Max(0, overlap.Height);
                if (size > bestArea)
                {
                    bestArea = size;
                    best = screen;
                }
            }
            if (best == null) best = WinForms.Screen.FromPoint(new DPoint(center.Left, center.Top));
            return best;
        }

        private void LoadWindowPosition()
        {
            try
            {
                string file = Path.Combine(Log.DataDir, "window.json");
                if (File.Exists(file))
                {
                    Dictionary<string, object> data = Json.Parse(File.ReadAllText(file, Encoding.UTF8));
                    if (data != null)
                    {
                        int left = Json.Int(data, "left", int.MinValue);
                        int top = Json.Int(data, "top", int.MinValue);
                        if (left != int.MinValue && top != int.MinValue)
                        {
                            desiredLeft = left;
                            desiredTop = top;
                        }
                    }
                }
            }
            catch (Exception error)
            {
                Log.Warn("window position load failed: " + error.Message);
            }
            if (desiredLeft == int.MinValue || desiredTop == int.MinValue) MoveToDefaultPosition();
        }

        private void MoveToDefaultPosition()
        {
            WinForms.Screen screen = WinForms.Screen.PrimaryScreen;
            DRect area = screen.WorkingArea;
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int width = (int)Math.Round(Width * dpi);
            int height = (int)Math.Round(Height * dpi);
            DRect current;
            if (hwnd != IntPtr.Zero && Native.TryGetWindowRect(hwnd, out current))
            {
                width = current.Width;
                height = current.Height;
            }
            int x = area.Right - width - 24;
            int y = area.Bottom - height - 24;
            desiredLeft = x;
            desiredTop = y;
            Log.Info("default position area=" + area.ToString() + " size=" + width + "x" + height
                + " desired=" + x + "," + y + " dpi=" + VisualTreeHelper.GetDpi(this).DpiScaleX.ToString("0.###", CultureInfo.InvariantCulture));
            if (hwnd != IntPtr.Zero)
            {
                Native.MoveWindow(hwnd, x, y);
                SaveWindowPosition();
            }
        }

        private void RestoreNativePosition()
        {
            DRect rect;
            if (!Native.TryGetWindowRect(hwnd, out rect))
            {
                MoveToDefaultPosition();
                return;
            }
            int x = rect.Left;
            int y = rect.Top;
            if (desiredLeft != int.MinValue && desiredTop != int.MinValue)
            {
                x = desiredLeft;
                y = desiredTop;
            }
            // Clamp into the current virtual screen; a monitor may have been
            // unplugged since the position was saved.
            DRect virtualScreen = Native.GetVirtualScreen();
            // Keep the whole window on-screen at startup.  Edge hiding is an
            // explicit gesture after a drag, not a side effect of saved DPI.
            x = Math.Max(virtualScreen.Left, Math.Min(x, virtualScreen.Right - rect.Width));
            y = Math.Max(virtualScreen.Top, Math.Min(y, virtualScreen.Bottom - rect.Height));
            Native.MoveWindow(hwnd, x, y);
            windowRect = new DRect(x, y, rect.Width, rect.Height);
            Log.Info("restore position current=" + rect.ToString() + " final=" + x + "," + y
                + " virtual=" + virtualScreen.ToString());
            desiredLeft = int.MinValue;
            desiredTop = int.MinValue;
        }

        private void SaveWindowPosition()
        {
            try
            {
                DRect rect;
                if (!Native.TryGetWindowRect(hwnd, out rect)) return;
                string dir = Log.DataDir;
                Directory.CreateDirectory(dir);
                Dictionary<string, object> data = new Dictionary<string, object> {
                    { "left", rect.Left }, { "top", rect.Top }, { "width", rect.Width }, { "height", rect.Height }
                };
                File.WriteAllText(Path.Combine(dir, "window.json"), Json.Serialize(data), new UTF8Encoding(false));
            }
            catch (Exception error)
            {
                Log.Warn("window position save failed: " + error.Message);
            }
        }

        private void OnDemoTick(object sender, EventArgs e)
        {
            if (demoStep % 3 == 0)
            {
                HandleAgent(new Dictionary<string, object> {
                    { "event", "start" }, { "text", "演示：开始整理文件" }
                });
            }
            else if (demoStep % 3 == 1)
            {
                HandleAgent(new Dictionary<string, object> {
                    { "event", "progress" }, { "text", "演示：第 3 步" }, { "tool", "pwsh" }, { "step", 3 }
                });
            }
            else
            {
                HandleAgent(new Dictionary<string, object> {
                    { "event", "complete" }, { "text", "演示：任务完成！" }
                });
            }
            demoStep++;
            UpdateDemoStats();
        }

        private void UpdateDemoStats()
        {
            if (!demo) return;
            Dictionary<string, object> statsMessage = new Dictionary<string, object> {
                { "line", "今日 ≈¥0.0123（演示）" },
                { "balance", "余额 ¥12.34（演示）" },
                { "detail", "演示数据，不会写入任何真实账单" },
                { "stale", false }
            };
            UpdateStats(statsMessage);
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            closing = true;
            if (bridge != null)
            {
                bridge.Send(new Dictionary<string, object> { { "type", "bye" } });
            }
            SaveWindowPosition();
            StopTimers();
            sounds.Dispose();
        }

        private void StopTimers()
        {
            DispatcherTimer[] timers = new DispatcherTimer[] {
                frameTimer, edgeTimer, fullscreenTimer, longPressTimer, bubbleTimer,
                chewTimer, demoTimer, progressHideTimer, bridgeLossTimer
            };
            foreach (DispatcherTimer timer in timers)
            {
                if (timer != null) timer.Stop();
            }
        }
    }
}
