// 示範 C# 側的查找。這個檔不會被建置（Demo 不是真的 App），
// 它存在的理由是讓 CSharpScanner 有東西可掃。

namespace Demo;

public static class CodeBehind
{
    public static object? Literal(System.Windows.FrameworkElement el)
    {
        // 字面鍵：算成引用。沒有這一行的話 CodeOnlyBrush 會被誤報成沒人用。
        return el.FindResource("CodeOnlyBrush");
    }

    public static object? Composed(System.Windows.FrameworkElement el, string name)
    {
        // 組出來的鍵名：靜態不可知。
        // ⚠ 工具必須把它計入 dynamicKeySites 並在 summary 講出來 ——
        //   不然 Icon* 底下的鍵會全部被報成沒人用,使用者照著刪就炸了。
        return el.FindResource($"Icon{name}");
    }

    // 註解裡的呼叫不該被當成引用（StripComments 先剝掉）：
    //   el.FindResource("CommentedOutKey");
}
