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

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("user32.dll")]
    static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr FindWindow(string className, string windowTitle);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowTitle);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    const uint LVM_FIRST = 0x1000;
    const uint LVM_GETSELECTEDCOUNT = LVM_FIRST + 50;
    const uint EM_GETSEL = 0x00B0;
    const uint EM_SETSEL = 0x00B1;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;      // フォーカスされているコントロール
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;  // メニューが開いていれば非ゼロ
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    // 指定ウィンドウのスレッドのGUI状態（フォーカス・メニュー等）を取得する
    static bool TryGetGuiInfo(IntPtr window, out GUITHREADINFO gti)
    {
        gti = new GUITHREADINFO();
        gti.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
        uint tid = GetWindowThreadProcessId(window, IntPtr.Zero);
        if (tid == 0) return false;
        return GetGUIThreadInfo(tid, ref gti);
    }

    static string GetClassNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(none)";
        StringBuilder sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // debug.txt がフォルダに存在するときだけ、起動時のGUI状態をログに残す。
    // 「fn+F7 がアプリに漏れてメニューを開いていないか」を実測するための診断。
    // menuOpen=YES なら、こちらのコードが動く前に既にメニューが開いている＝キー漏れの証拠。
    static void LogDiagnostic(string phase, IntPtr window)
    {
        try
        {
            string marker = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.ExecutablePath), "debug.txt");
            if (!System.IO.File.Exists(marker)) return;

            GUITHREADINFO g;
            string focusClass = "?";
            string menu = "?";
            if (TryGetGuiInfo(window, out g))
            {
                focusClass = GetClassNameOf(g.hwndFocus);
                menu = (g.hwndMenuOwner != IntPtr.Zero) ? "YES" : "no";
            }
            LogMessage("DEBUG[" + phase + "] fg=" + GetClassNameOf(window)
                + " focus=" + focusClass + " menuOpen=" + menu);
        }
        catch { /* 診断ログの失敗は本処理に影響させない */ }
    }

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_UNICODE = 0x0004;
    const ushort VK_F2 = 0x71;
    const ushort VK_RIGHT = 0x27;
    const ushort VK_ESCAPE = 0x1B;

    // タイミング調整用の待機時間。
    // 注意: 秀丸のように「起動キー（fn+F7）がアプリ側に漏れる」タイプのアプリでは、
    // この値をいくら伸ばしても先頭文字の欠け・メニュー誤起動は直らない。
    // それはコード外（Logiのキー割り当てがアプリに素通しする）の問題であり、
    // 根本対処はLogi側でキーをF13〜F24等の「どのアプリでも無反応なキー」に変えること。
    // ここの数値いじりで解決しようとしないこと。
    const int WAIT_BEFORE_TYPING_MS = 1000; // 起動キーの状態が収まり、対象にフォーカスが戻るまで
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

    // F2 送出後、リネーム編集に安全に入れたと判断できるかを確認する。
    // 破壊操作（ファイル名変更）の直前チェックなので、確信が持てないときは false を返す。
    // ・フォアグラウンドが依然としてエクスプローラーであること
    // ・メニューが開いていないこと（メニューが開いている＝F2以外の何かが起きた）
    // ※フォーカスコントロールのクラス判定は Windows のバージョン差で誤判定の恐れがあるため
    //   ここではあえて条件にせず、診断ログ側にのみ記録している。
    static bool IsRenameEditReady()
    {
        IntPtr fg = GetForegroundWindow();
        if (!IsExplorerWindow(fg)) return false;

        GUITHREADINFO g;
        if (TryGetGuiInfo(fg, out g) && g.hwndMenuOwner != IntPtr.Zero) return false;

        return true;
    }

    // リネーム編集ボックスの「現在の選択範囲の終端」にカーソルを移動する。
    // F2直後の自動選択（ファイルなら拡張子の前まで、フォルダなら名前全体）の
    // 終端に確実にカーソルを置けるため、右矢印キーのような当たり判定のズレが起きない。
    // 成功したら true。標準の編集ボックスでない・選択が無い等で扱えないときは false。
    static bool PlaceCaretAtSelectionEnd()
    {
        IntPtr fg = GetForegroundWindow();
        GUITHREADINFO g;
        if (!TryGetGuiInfo(fg, out g)) return false;

        IntPtr edit = g.hwndFocus;
        if (edit == IntPtr.Zero) return false;

        // EM_GETSEL の戻り値は 下位16bit=選択開始, 上位16bit=選択終了
        IntPtr sel = SendMessage(edit, EM_GETSEL, IntPtr.Zero, IntPtr.Zero);
        int packed = sel.ToInt32();
        int selStart = packed & 0xFFFF;
        int selEnd = (packed >> 16) & 0xFFFF;

        // 選択が無い（開始＝終了）場合は、標準の編集ボックスでない可能性が高いので false
        if (selStart == selEnd) return false;

        // 選択を解除して終端にカーソルを置く
        SendMessage(edit, EM_SETSEL, (IntPtr)selEnd, (IntPtr)selEnd);
        return true;
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

    // 前面ウィンドウがデスクトップ（Progman / WorkerW）かどうか
    static bool IsDesktopWindow(IntPtr hwnd)
    {
        string cls = GetClassNameOf(hwnd);
        return cls == "Progman" || cls == "WorkerW";
    }

    // デスクトップのアイコン一覧（SysListView32）のハンドルを探す。
    // 通常は Progman → SHELLDLL_DefView → SysListView32。
    // 壁紙描画の都合で WorkerW の下にぶら下がることもあるため、両方を探す。
    static IntPtr GetDesktopListView()
    {
        IntPtr defView = FindWindowEx(FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            IntPtr worker = IntPtr.Zero;
            while (defView == IntPtr.Zero)
            {
                worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
                if (worker == IntPtr.Zero) break;
                defView = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            }
        }
        if (defView == IntPtr.Zero) return IntPtr.Zero;
        return FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    // デスクトップで選択されている項目数を返す。判定不能なら -1。
    // Shell.Application のウィンドウ一覧にデスクトップは含まれないため、
    // SysListView32 に LVM_GETSELECTEDCOUNT を直接送って数える。
    static int GetDesktopSelectedCount()
    {
        IntPtr lv = GetDesktopListView();
        if (lv == IntPtr.Zero) return -1;
        return SendMessage(lv, LVM_GETSELECTEDCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
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

            // 起動直後のGUI状態を診断（debug.txt があるときだけ記録）
            LogDiagnostic("start", target);

            bool isExplorer = IsExplorerWindow(target);

            // 項目（ファイル／フォルダ）がちょうど1件選択されている場合のみリネーム動作を行う。
            // 0件（ブラウズ中）・複数選択・判定不能な場合は何もしない。
            // 通常のエクスプローラーウィンドウは Shell.Application 経由で数えるが、
            // デスクトップはそこに含まれないため SysListView32 から直接数える。
            int selectedCount = GetExplorerSelectedCount(target);
            if (selectedCount < 0 && IsDesktopWindow(target))
            {
                selectedCount = GetDesktopSelectedCount();
            }
            bool shouldRename = isExplorer && selectedCount == 1;

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

                // F2でリネーム編集に入れたかを確認してから右矢印＋タイプする。
                // 入れていない（フォアグラウンドがExplorerでない／メニューが開いている）まま
                // 進むと、ファイルリスト上でキー入力が暴発する恐れがあるため中断する。
                LogDiagnostic("after-F2", GetForegroundWindow());
                if (!IsRenameEditReady())
                {
                    NotifyFailure("リネーム編集に入れなかったため中断（誤入力防止）");
                    return;
                }

                // F2直後は「拡張子を除いた名前部分」（フォルダなら名前全体）が選択されている。
                // 選択の終端を編集ボックスに直接問い合わせ、そこへカーソルを確実に置く。
                // 右矢印キーでの移動は環境によりドットを1つ飛び越える誤差が出るため使わない。
                // 標準の編集ボックスでない等で選択が取れない場合のみ、従来の右矢印にフォールバックする。
                if (!PlaceCaretAtSelectionEnd())
                {
                    SendVKey(VK_RIGHT);
                    Thread.Sleep(WAIT_AFTER_RIGHT_MS);
                }
            }
            else
            {
                // 秀丸などでは、起動キー（fn+F7）の影響でフォーカスが本文ではなく
                // メニューバー／ツールバー（ToolbarWindow32）に逃げていることがある。
                // その状態でタイプすると先頭文字がメニューバーに飲まれて欠ける。
                // ESC を1回送るとメニューバーに乗ったフォーカスが解除され本文に戻る。
                // エディタ本文での ESC は無害（何も起きない）。
                // ※エクスプローラーのリネーム中は ESC が取り消しになるので、この経路（else）でのみ送る。
                SendVKey(VK_ESCAPE);
                Thread.Sleep(WAIT_AFTER_RIGHT_MS);
            }

            LogDiagnostic("pre-type", GetForegroundWindow());
            TypeText(today);
        }
        catch (Exception ex)
        {
            NotifyFailure(ex.Message);
        }
    }
}
