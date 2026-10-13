namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The client's root view; shows whichever screen the view model selects.
/// </summary>
public sealed partial class MainView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MainView" /> class.
    /// </summary>
    public MainView() => InitializeComponent();

    private IInputPane? inputPane;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        inputPane = TopLevel.GetTopLevel(this)?.InputPane;

        if (inputPane is not null)
        {
            inputPane.StateChanged += Resize;
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (inputPane is not null)
        {
            inputPane.StateChanged -= Resize;
            inputPane = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void Resize(object? sender, InputPaneStateEventArgs e) => this.FindControl<Border>("Root")!.Padding = new Thickness(0, 0, 0, e.NewState == InputPaneState.Open ? e.EndRect.Height : 0);
}
