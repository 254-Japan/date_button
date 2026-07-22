using System;
using System.Text;
using System.Windows.Forms;
using System.Threading;
using System.Runtime.InteropServices;

// 今日の日付を YYYYMMDD 形式でクリップボードにコピーし、Ctrl+V で貼り付ける
// - クリップボード経由なので必ず半角（全角にならない）
// - Ctrl+V は keybd_event の実キー入力で送るため、サクラエディタ等でも確実
// - エクスプローラーで「ファイルが1件だけ選択されている」ときに限り、
//   F2 でリネームを開いて名前の末尾（拡張子の前）に日付を追加する。
//   0件・複数選択・判定不能なときは何もリネーム操作をせず、通常の貼り付けのみ行う
//   （フェイルクローズ：意図が確認できない操作はしない）
// - 失敗時は無言で終了せず、ビープ音＋非モーダルの通知バルーンで気づけるようにする
// Logi Options+ の「アプリケーションを開く」で PasteDate.exe を指定して使う
class PasteDate
{
    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    const uint KEYEVENTF_KEYUP = 0x0002;
    const byte VK_CONTROL = 0x11;
    const byte VK_V = 0x56;
    const byte VK_F2 = 0x71;
    const byte VK_RIGHT = 0x27;

    // タイミング調整用の待機時間（環境によって足りない場合はここを伸ばす）
    const int WAIT_AFTER_CLIPBOARD_MS = 700;    // クリップボード確定待ち（Logi側のキー状態が完全に収まるまで）
    const int WAIT_AFTER_FOCUS_MS = 80;         // フォーカス復帰待ち
    const int WAIT_AFTER_F2_MS = 150;           // リネーム編集モード開始待ち
    const int WAIT_AFTER_RIGHT_MS = 50;         // カーソル移動反映待ち

    // エクスプローラーのファイルリスト／デスクトップのウィンドウクラス名
    static readonly string[] ExplorerClassNames = { "CabinetWClass", "ExploreWClass", "Progman", "WorkerW" };

    static void SendKeyEvent(byte vk, bool up)
    {
        keybd_event(vk, 0, up ? KEYEVENTF_KEYUP : 0, UIntPtr.Zero);
    }

    static void SendKey(byte vk)
    {
        SendKeyEvent(vk, false);
        Thread.Sleep(15);
        SendKeyEvent(vk, true);
    }

    static void SendCtrlV()
    {
        SendKeyEvent(VK_CONTROL, false); // Ctrl押下
        SendKeyEvent(VK_V, false);       // V押下
        Thread.Sleep(30);
        SendKeyEvent(VK_V, true);        // V離す
        SendKeyEvent(VK_CONTROL, true);  // Ctrl離す
    }

    static bool IsExplorerWindow(IntPtr hwnd)
    {
        StringBuilder sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        string className = sb.ToString();
        foreach (string name in ExplorerClassNames)
        {
            if (className == name) return true;
        }
        return false;
    }

    // エクスプローラーで選択されているファイル件数を取得する。
    // Shell.Application 経由で該当ウィンドウを探すため、内部コントロールの
    // 実装（DirectUIHWND / SysListView32 など）に依存せず安定して数えられる。
    // 判定できない場合は -1 を返し、呼び出し側は「わからない＝実行しない」と扱う。
    static int GetExplorerSelectedCount(IntPtr hwnd)
    {
        try
        {
            Type shellAppType = Type.GetTypeFromProgID("Shell.Application");
            if (shellAppType == null) return -1;

            dynamic shellApp = Activator.CreateInstance(shellAppType);
            dynamic windows = shellApp.Windows();
            int count = windows.Count;

            for (int i = 0; i < count; i++)
            {
                dynamic win = windows.Item(i);
                if (win == null) continue;

                IntPtr winHwnd;
                try { winHwnd = new IntPtr((int)win.HWND); }
                catch { continue; }

                if (winHwnd == hwnd)
                {
                    dynamic doc = win.Document;
                    dynamic selectedItems = doc.SelectedItems();
                    return (int)selectedItems.Count;
                }
            }
        }
        catch
        {
            // Shell.Application 経由での取得に失敗した場合は判定不能として扱う
            return -1;
        }
        return -1; // 対象ウィンドウが見つからなかった（デスクトップ等）
    }

    // 対象ウィンドウを前面に戻す。成功したかどうかを返す。
    // 失敗時に後続のキー送信を行うと、見当違いのウィンドウに入力してしまうため
    // 呼び出し側で戻り値を必ず確認すること。
    static bool RestoreFocus(IntPtr target)
    {
        if (target == IntPtr.Zero) return false;

        // すでに対象ウィンドウが前面にある場合（＝Logi経由でフォーカスが
        // 移動していない通常のケース）は、AttachThreadInput等の操作をせず
        // そのまま何もしない。不要なフォーカス操作は一部アプリ（サクラエディタ等）の
        // 入力状態を乱し、直後のCtrl+Vが無視される原因になり得るため。
        if (GetForegroundWindow() == target) return true;

        uint targetThread = GetWindowThreadProcessId(target, IntPtr.Zero);
        uint currentThread = GetCurrentThreadId();

        bool attached = AttachThreadInput(currentThread, targetThread, true);
        bool result = SetForegroundWindow(target);
        if (attached)
        {
            AttachThreadInput(currentThread, targetThread, false);
        }

        Thread.Sleep(WAIT_AFTER_FOCUS_MS);
        return result && GetForegroundWindow() == target;
    }

    // 失敗をビープ音で知らせつつ、原因をログファイルに記録する。
    // バルーン通知はメッセージループが無いと表示されないことがあり信頼できないため、
    // 確実に後から確認できるログ方式にしている。
    static void NotifyFailure(string reason)
    {
        Console.Beep(400, 300);

        try
        {
            string logPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.ExecutablePath),
                "error_log.txt");
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + reason + Environment.NewLine;
            System.IO.File.AppendAllText(logPath, line);
        }
        catch
        {
            // ログ書き込み自体に失敗しても、ビープ音だけは鳴っているので致命的ではない
        }
    }

    [STAThread]
    static void Main()
    {
        try
        {
            // 起動時点で前面にあったウィンドウを記憶
            IntPtr target = GetForegroundWindow();
            if (target == IntPtr.Zero)
            {
                NotifyFailure("入力先のウィンドウが見つかりませんでした");
                return;
            }

            bool isExplorer = IsExplorerWindow(target);

            // ファイルがちょうど1件選択されている場合のみリネーム動作を行う。
            // 0件（ブラウズ中）・複数選択・判定不能な場合は何もしない。
            bool shouldRename = isExplorer && GetExplorerSelectedCount(target) == 1;

            string today = DateTime.Now.ToString("yyyyMMdd");
            Clipboard.SetText(today);
            Thread.Sleep(WAIT_AFTER_CLIPBOARD_MS);

            if (!RestoreFocus(target))
            {
                NotifyFailure("元のウィンドウにフォーカスを戻せませんでした");
                return;
            }

            if (shouldRename)
            {
                // F2直後は拡張子を除いた名前部分が選択されるので、右矢印で
                // 選択の右端（拡張子の直前）にカーソルを移動して日付を追加する
                SendKey(VK_F2);
                Thread.Sleep(WAIT_AFTER_F2_MS);
                SendKey(VK_RIGHT);
                Thread.Sleep(WAIT_AFTER_RIGHT_MS);
            }

            SendCtrlV();
        }
        catch (Exception ex)
        {
            NotifyFailure(ex.Message);
        }
    }
}
