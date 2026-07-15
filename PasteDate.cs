using System;
using System.Text;
using System.Windows.Forms;
using System.Threading;
using System.Runtime.InteropServices;

// 今日の日付を YYYYMMDD 形式でクリップボードにコピーし、Ctrl+V で貼り付ける
// - クリップボード経由なので必ず半角（全角にならない）
// - Ctrl+V は SendInput の実キー入力で送るため、サクラエディタ等でも確実
// - 起動時の前面ウィンドウがエクスプローラー系（ファイルウィンドウ／デスクトップ）の
//   場合は、他プロセス起動でリネーム編集が閉じるため F2 で開き直してから貼り付ける
// - 失敗時は無言で終了せず、ビープ音とバルーン風メッセージで気づけるようにする
// Logi Options+ の「アプリケーションを開く」で PasteDate.exe を指定して使う
class PasteDate
{
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
        public int padding1;
        public int padding2;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

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

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const ushort VK_CONTROL = 0x11;
    const ushort VK_V = 0x56;
    const ushort VK_F2 = 0x71;
    const ushort VK_RIGHT = 0x27;

    // タイミング調整用の待機時間（環境によって足りない場合はここを伸ばす）
    const int WAIT_AFTER_CLIPBOARD_MS = 250;    // クリップボード確定待ち
    const int WAIT_AFTER_FOCUS_MS = 80;         // フォーカス復帰待ち
    const int WAIT_AFTER_F2_MS = 150;           // リネーム編集モード開始待ち
    const int WAIT_AFTER_RIGHT_MS = 50;         // カーソル移動反映待ち

    // エクスプローラーのファイルリスト／デスクトップのウィンドウクラス名
    // （ここに来た場合のみ F2 でリネームを開き直す）
    static readonly string[] ExplorerClassNames = { "CabinetWClass", "ExploreWClass", "Progman", "WorkerW" };

    static INPUT Key(ushort vk, bool up)
    {
        INPUT i = new INPUT();
        i.type = INPUT_KEYBOARD;
        i.ki.wVk = vk;
        i.ki.dwFlags = up ? KEYEVENTF_KEYUP : 0;
        return i;
    }

    static void SendKey(ushort vk)
    {
        INPUT[] inputs = new INPUT[2];
        inputs[0] = Key(vk, false);
        inputs[1] = Key(vk, true);
        SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    static void SendCtrlV()
    {
        INPUT[] inputs = new INPUT[4];
        inputs[0] = Key(VK_CONTROL, false);
        inputs[1] = Key(VK_V, false);
        inputs[2] = Key(VK_V, true);
        inputs[3] = Key(VK_CONTROL, true);
        SendInput(4, inputs, Marshal.SizeOf(typeof(INPUT)));
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

    // 対象ウィンドウを前面に戻す。成功したかどうかを返す。
    // 失敗時に後続のキー送信を行うと、見当違いのウィンドウに入力してしまうため
    // 呼び出し側で戻り値を必ず確認すること。
    static bool RestoreFocus(IntPtr target)
    {
        if (target == IntPtr.Zero) return false;

        uint targetThread = GetWindowThreadProcessId(target, IntPtr.Zero);
        uint currentThread = GetCurrentThreadId();

        bool attached = AttachThreadInput(currentThread, targetThread, true);
        bool result = SetForegroundWindow(target);
        if (attached)
        {
            AttachThreadInput(currentThread, targetThread, false);
        }

        // SetForegroundWindow成功後、実際にそのウィンドウが前面になったかを確認する
        Thread.Sleep(WAIT_AFTER_FOCUS_MS);
        return result && GetForegroundWindow() == target;
    }

    static void NotifyFailure(string reason)
    {
        Console.Beep(400, 300);
        MessageBox.Show(
            "日付の入力に失敗しました。\n\n理由: " + reason + "\n\nもう一度キーを押してみてください。",
            "PasteDate",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
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

            string today = DateTime.Now.ToString("yyyyMMdd");
            Clipboard.SetText(today);
            Thread.Sleep(WAIT_AFTER_CLIPBOARD_MS);

            if (!RestoreFocus(target))
            {
                NotifyFailure("元のウィンドウにフォーカスを戻せませんでした");
                return;
            }

            // エクスプローラー系なら F2 でリネームを開き直す。
            // F2直後は拡張子を除いた名前部分が選択されるので、右矢印で
            // 選択の右端（拡張子の直前）にカーソルを移動して日付を追加する
            if (isExplorer)
            {
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
