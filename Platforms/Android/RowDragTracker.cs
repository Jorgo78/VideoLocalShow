using Android.Content;
using Android.Views;
using AndroidX.RecyclerView.Widget;
using Microsoft.Maui.Controls.Handlers.Items;

namespace VideoLocalShow;

/// <summary>
/// Lets the Downloads page know which row has been picked up for drag-and-drop reordering, so it
/// can tint it until it is let go. The CollectionView's own reorder (CanReorderItems) does the
/// moving but exposes neither when a drag starts nor which row it is.
///
/// Its drag starts on a long press, so this spots the same long press (same timeout, via a
/// GestureDetector fed from a RecyclerView touch listener that never intercepts anything) and
/// reports the row under the finger. The end of a drag can't be seen from that listener - once the
/// reorder has taken over the gesture, the touch events stop reaching it - so MainActivity reports
/// every finger-up / cancel through <see cref="NotifyTouchEnded"/> instead.
/// </summary>
public sealed class RowDragTracker : Java.Lang.Object, RecyclerView.IOnItemTouchListener
{
    private readonly GestureDetector _detector;
    private readonly Action<object?> _onRowLongPressed;
    private RecyclerView? _recyclerView;

    public RowDragTracker(Context context, Action<object?> onRowLongPressed)
    {
        _onRowLongPressed = onRowLongPressed;
        _detector = new GestureDetector(context, new LongPressListener(this));
    }

    /// <summary>Raised whenever a touch ends (finger lifted, or the gesture cancelled).</summary>
    public static event Action? TouchEnded;

    public static void NotifyTouchEnded() => TouchEnded?.Invoke();

    public bool OnInterceptTouchEvent(RecyclerView rv, MotionEvent e)
    {
        _recyclerView = rv;
        _detector.OnTouchEvent(e);

        // Never take the gesture over: scrolling, taps and the reorder's own drag all still need it.
        return false;
    }

    public void OnTouchEvent(RecyclerView rv, MotionEvent e)
    {
    }

    public void OnRequestDisallowInterceptTouchEvent(bool disallow)
    {
    }

    private sealed class LongPressListener(RowDragTracker owner) : GestureDetector.SimpleOnGestureListener
    {
        public override void OnLongPress(MotionEvent e)
        {
            if (owner._recyclerView is not { } recyclerView)
            {
                return;
            }

            var child = recyclerView.FindChildViewUnder(e.GetX(), e.GetY());
            if (child is null)
            {
                return;
            }

            // The row's data item, as bound to the MAUI view inside this RecyclerView row.
            var item = (recyclerView.GetChildViewHolder(child) as TemplatedItemViewHolder)?.View?.BindingContext;
            owner._onRowLongPressed(item);
        }
    }
}
