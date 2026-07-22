using System;
using System.Text;
using System.Windows.Forms;
using System.Threading;
using System.Runtime.InteropServices;

// 今日の日付を YYYYMMDD 形式で、アクティブなウィンドウに「直接タイプ入力」する。
//
// 設計方針（クリップボード＋Ctrl+V 方式から変更した理由）:
// - クリップボードを一切使わない
//     秀丸などクリップボード監視機能を持つアプリと競合し、貼り付けが失敗していたため。
// - Ctrl / Alt などの修飾キーを一切使わない
//     修飾キーの残留・単独送信が「ファイル」メニュー誤起動やキー競合の原因だったため。
// - SendInput の KEYEVENTF_UNICODE で文字コードを直接送る
//     IME の ON/OFF や全角モードに影響されず、必ず半角数字で入力される。
// - エクスプローラーで「項目（ファイル／フォルダ）が1件だけ選択されている」ときに限り、
//     F2 でリネームを開き、右矢印で名前の末尾（ファイルなら拡張子の直前）へ移動して
//     から日付をタイプする。0件・複数選択・判定不能なときはリネーム操作をしない
//     （フェイルクローズ：意図が確認できない操作はしない）。
// - 失敗時は無言で終了せず、ビープ音＋error_log.txt への記録で原因を追えるようにする。
//
// Logi Options+ の「アプリケーションを開く」で PasteDate.exe を指定して使う。
class PasteDate
{
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
        // マウス/ハードウェア入力共用体分のパディング（最大サイズに合わせる）
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

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_UNICODE = 0x0004;
    const ushort VK_F2 = 0x71;
    const ushort VK_RIGHT = 0x27;

    // タイミング調整用の待機時間（環境によって足りない場合はここを伸ばす）
    const int WAIT_BEFORE_TYPING_MS = 1000; // 起動キーの状態が収まり、対象にフォーカスが戻るまで（短いと先頭文字が欠ける）
    const int WAIT_AFTER_F2_MS = 150;       // リネーム編集モード開始待ち
    const int WAIT_AFTER_RIGHT_MS = 50;     // カーソル移動反映待ち
    const int WAIT_BETWEEN_KEYS_MS = 15;    // キー間隔

    // エクスプローラーのファイルリスト／デスクトップのウィンドウクラス名
    static readonly string[] ExplorerClassNames = { "CabinetWClass", "ExploreWClass", "Progman", "WorkerW" };

    // 仮想キー（F2・右矢印など）を1回押して離す
    static void SendVKey(ushort vk)
    {
        INPUT[] inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = vk;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wVk = vk;
        inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    // 1文字を Unicode としてそのまま送る（IME・全角モードに影響されない）
    static void TypeUnicodeChar(char c)
    {
        INPUT[] inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wScan = c;
        inputs[0].ki.dwFlags = KEYEVENTF_UNICODE;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wScan = c;
        inputs[1].ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    static void TypeText(string text)
    {
        foreach (char c in text)
        {
            TypeUnicodeChar(c);
            Thread.Sleep(WAIT_BETWEEN_KEYS_MS);
        }
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

    // エクスプローラーで選択されている項目（ファイル／フォルダ）の件数を取得する。
    // Shell.Application 経由で該当ウィンドウを探すため、内部コントロールの
    // 実装（DirectUIHWND / SysListView32 など）に依存せず安定して数えられる。
    // 判定できない場合は -1 を返し、呼び出し側は「わからない＝実行しない」と扱う。
    // ファイルもフォルダも、1件選択ならリネーム対象とする（F2→右矢印→末尾に日付追加）。
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
                try { winHwnd = new IntPtr((long)win.HWND); }
                catch { continue; }

                if (winHwnd == hwnd)
                {
                    dynamic doc = win.Document;
                    dynamic selectedItems = doc.SelectedItems();
                    return (int)selectedItems.Count;
                }
            }
        }
        catch (Exception ex)
        {
            // Shell.Application 経由での取得に失敗した場合は判定不能として扱うが、
            // 原因が追えるようログには残す（無言で-1を返すと次回同じ不具合の調査ができない）
            LogMessage("選択件数の取得に失敗（判定不能として扱う）: " + ex.Message);
            return -1;
        }
        return -1; // 対象ウィンドウが見つからなかった（デスクトップ等）
    }

    const int LOG_MAX_LINES = 200; // これを超えたら古い行から間引く

    static string LogPath()
    {
        return System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Application.ExecutablePath),
            "error_log.txt");
    }

    // ログに1行追記する。無制限に肥大化しないよう、上限を超えたら古い行を捨てる。
    static void LogMessage(string message)
    {
        try
        {
            string path = LogPath();
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message;

            var lines = System.IO.File.Exists(path)
                ? new System.Collections.Generic.List<string>(System.IO.File.ReadAllLines(path))
                : new System.Collections.Generic.List<string>();

            lines.Add(line);
            if (lines.Count > LOG_MAX_LINES)
            {
                lines.RemoveRange(0, lines.Count - LOG_MAX_LINES);
            }

            System.IO.File.WriteAllLines(path, lines);
        }
        catch
        {
            // ログファイル自体の読み書きに失敗した場合、これ以上記録する手段がないため
            // ここは意図的に諦める（呼び出し元でビープ音は別途鳴らしている）
        }
    }

    // 失敗をビープ音で知らせつつ、原因をログファイルに記録する。
    static void NotifyFailure(string reason)
    {
        Console.Beep(400, 300);
        LogMessage(reason);
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

            // 項目（ファイル／フォルダ）がちょうど1件選択されている場合のみリネーム動作を行う。
            // 0件（ブラウズ中）・複数選択・判定不能な場合は何もしない。
            bool shouldRename = isExplorer && GetExplorerSelectedCount(target) == 1;

            string today = DateTime.Now.ToString("yyyyMMdd");

            // 起動キー（Logiのfn+F7等）の押下状態が完全に収まり、
            // 対象ウィンドウにフォーカスが戻るのを待つ。
            Thread.Sleep(WAIT_BEFORE_TYPING_MS);

            if (shouldRename)
            {
                // F2直後は拡張子を除いた名前部分が選択されるので、右矢印で
                // 選択の右端（拡張子の直前）にカーソルを移動して日付を追加する
                SendVKey(VK_F2);
                Thread.Sleep(WAIT_AFTER_F2_MS);
                SendVKey(VK_RIGHT);
                Thread.Sleep(WAIT_AFTER_RIGHT_MS);
            }

            TypeText(today);
        }
        catch (Exception ex)
        {
            NotifyFailure(ex.Message);
        }
    }
}
