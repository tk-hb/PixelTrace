using Android.Content;
using Android.Views;
using Android.Widget;

namespace PixelTrace.Mobile;

public sealed class SearchActionsView : ViewGroup
{
    private Button Compare { get; }
    private Button Mode { get; }
    private Button Search { get; }

    private int rows;
    private int rowHeight, modeWidth, searchWidth;
    public SearchActionsView(Context context, Button compare, Button mode, Button search) : base(context)
    {
        Compare = compare;
        Mode = mode;
        Search = search;
        AddView(compare);
        AddView(mode);
        AddView(search);
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int width = MeasureSpec.GetSize(widthMeasureSpec);
        int gap = Ui.Dp(Context!, 8);
        modeWidth = Math.Max(Ui.Dp(Context!, 125), DesiredWidth(Mode));
        searchWidth = Math.Max(Ui.Dp(Context!, 66), DesiredWidth(Search));
        rows = width < DesiredWidth(Compare) + modeWidth + searchWidth + gap * 2 ? 2 : 1;
        rowHeight = Math.Max(Ui.Dp(Context!, 48), new[] { Compare, Mode, Search }.Max((button) => (int)Math.Ceiling(button.Paint!.FontSpacing) + Ui.Dp(Context!, 24)));
        if (rows == 2 && (width - gap) / 2 < Math.Max(DesiredWidth(Compare), modeWidth))
        {
            rows = 3;
        }

        if (1 < rows)
        {
            modeWidth = rows == 3 ? width : (width - gap) / 2;
            searchWidth = width;
        }

        MeasureButton(Compare, rows == 3 ? width : rows == 2 ? width - modeWidth - gap : width - modeWidth - searchWidth - gap * 2);
        MeasureButton(Mode, modeWidth);
        MeasureButton(Search, searchWidth);
        SetMeasuredDimension(width, ResolveSize(rowHeight * rows + gap * (rows - 1), heightMeasureSpec));
    }

    private int DesiredWidth(Button button)
        => (int)Math.Ceiling(button.Paint!.MeasureText(button.Text ?? "")) + button.CompoundPaddingLeft + button.CompoundPaddingRight;
    private void MeasureButton(Button button, int width)
        => button.Measure(MeasureSpec.MakeMeasureSpec(Math.Max(0, width), MeasureSpecMode.Exactly), MeasureSpec.MakeMeasureSpec(rowHeight, MeasureSpecMode.Exactly));
    protected override void OnLayout(bool changed, int left, int top, int right, int bottom)
    {
        int gap = Ui.Dp(Context!, 8);
        Compare.Layout(0, 0, Compare.MeasuredWidth, rowHeight);
        int modeLeft = rows == 3 ? 0 : Compare.MeasuredWidth + gap;
        int modeTop = rows == 3 ? rowHeight + gap : 0;
        Mode.Layout(modeLeft, modeTop, modeLeft + modeWidth, modeTop + rowHeight);
        int searchLeft = 1 < rows ? 0 : modeLeft + modeWidth + gap;
        int searchTop = (rows - 1) * (rowHeight + gap);
        Search.Layout(searchLeft, searchTop, searchLeft + searchWidth, searchTop + rowHeight);
    }
}
