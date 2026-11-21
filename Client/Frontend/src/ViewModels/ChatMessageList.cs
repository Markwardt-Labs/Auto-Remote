namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The messages a chat shows, with operations that change many at once and tell the view of it with a single notification, so a view that builds an element for every message does its work once for a chunk instead of once for each message.
/// </summary>
public interface IChatMessageList : IList<ChatMessage>, INotifyCollectionChanged
{
    /// <summary>
    /// Replaces every message.
    /// </summary>
    /// <param name="messages">The messages that remain, in order.</param>
    void Reset(IReadOnlyList<ChatMessage> messages);

    /// <summary>
    /// Inserts messages in front of the ones shown.
    /// </summary>
    /// <param name="messages">The messages, in order.</param>
    void InsertFirst(IReadOnlyList<ChatMessage> messages);

    /// <summary>
    /// Removes the oldest messages.
    /// </summary>
    /// <param name="count">How many to remove.</param>
    void RemoveFirst(int count);
}

/// <inheritdoc cref="IChatMessageList" />
public sealed class ChatMessageList : ObservableCollection<ChatMessage>, IChatMessageList
{
    /// <inheritdoc />
    public void Reset(IReadOnlyList<ChatMessage> messages)
    {
        Items.Clear();

        foreach (ChatMessage message in messages)
        {
            Items.Add(message);
        }

        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <inheritdoc />
    public void InsertFirst(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        for (int index = 0; index < messages.Count; index++)
        {
            Items.Insert(index, messages[index]);
        }

        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, messages.ToList(), 0));
    }

    /// <inheritdoc />
    public void RemoveFirst(int count)
    {
        count = Math.Min(count, Count);

        if (count <= 0)
        {
            return;
        }

        List<ChatMessage> removed = [.. Items.Take(count)];

        for (int index = 0; index < count; index++)
        {
            Items.RemoveAt(0);
        }

        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, 0));
    }

    private void Changed(NotifyCollectionChangedEventArgs args)
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(args);
    }
}
