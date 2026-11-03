namespace Markwardt.AutoRemote.Client;

/// <summary>
/// One setting of the selected AI model in the workspace settings, shown as a list, a switch or a number according to its kind.
/// </summary>
/// <param name="info">The setting as the server describes it.</param>
/// <param name="value">The value the workspace uses.</param>
/// <param name="changed">Called with the new value after the user changes it.</param>
internal sealed class ModelSettingViewModel(ModelSettingInfo info, string value, Action<string, string> changed) : ViewModelBase
{
    private string current = value;

    /// <summary>
    /// Gets the identifier of the setting.
    /// </summary>
    public string Id => info.Id;

    /// <summary>
    /// Gets the name shown for the setting.
    /// </summary>
    public string Name => info.Name;

    /// <summary>
    /// Gets what the setting does, or an empty text.
    /// </summary>
    public string Description => info.Description ?? string.Empty;

    /// <summary>
    /// Gets a value indicating whether the setting has a description to show.
    /// </summary>
    public bool HasDescription => info.Description is { Length: > 0 };

    /// <summary>
    /// Gets a value indicating whether the setting is one of a list of values.
    /// </summary>
    public bool IsChoice => info.Kind == ModelSettingKind.Choice;

    /// <summary>
    /// Gets a value indicating whether the setting is on or off.
    /// </summary>
    public bool IsToggle => info.Kind == ModelSettingKind.Toggle;

    /// <summary>
    /// Gets a value indicating whether the setting is a number.
    /// </summary>
    public bool IsNumber => info.Kind == ModelSettingKind.Number;

    /// <summary>
    /// Gets the values of a list setting, from the lowest to the highest.
    /// </summary>
    public IReadOnlyList<string> Choices => info.Choices;

    /// <summary>
    /// Gets the smallest value of a number setting.
    /// </summary>
    public decimal Minimum => info.Minimum ?? 0;

    /// <summary>
    /// Gets the largest value of a number setting.
    /// </summary>
    public decimal Maximum => info.Maximum ?? int.MaxValue;

    /// <summary>
    /// Gets how much the arrows of a number setting step: coarser for a wide range.
    /// </summary>
    public decimal Increment => Maximum - Minimum > 4096 ? 1024 : 1;

    /// <summary>
    /// Gets or sets the value of a list setting; setting it saves it.
    /// </summary>
    public string ChoiceValue
    {
        get => current;
        set => Change(value);
    }

    /// <summary>
    /// Gets or sets whether a switch setting is on; setting it saves it.
    /// </summary>
    public bool ToggleValue
    {
        get => current == "true";
        set => Change(value ? "true" : "false");
    }

    /// <summary>
    /// Gets or sets the value of a number setting; setting it saves it.
    /// </summary>
    public decimal? NumberValue
    {
        get => decimal.TryParse(current, NumberStyles.Integer, CultureInfo.InvariantCulture, out decimal number) ? number : null;
        set
        {
            if (value is { } number)
            {
                Change(decimal.Truncate(number).ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private void Change(string next)
    {
        if (next == current || !info.Accepts(next))
        {
            return;
        }

        current = next;
        Raise(nameof(ChoiceValue));
        Raise(nameof(ToggleValue));
        Raise(nameof(NumberValue));
        changed(info.Id, next);
    }
}
