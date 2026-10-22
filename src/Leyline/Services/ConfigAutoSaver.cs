using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;

namespace Leyline.Services;

/// <summary>
/// Saves the configuration shortly after a saved setting changes (a mute, volume, send, effect, assigned app...),
/// so changes survive a crash, a forced close or Windows shutting down, not only a normal exit. Watches every object
/// reachable from the config that raises change notifications; properties that are not saved ([JsonIgnore], such as
/// meters and status text) are ignored.
/// </summary>
public sealed class ConfigAutoSaver
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(750);
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> SavedProperties = new();

    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _timer;
    private readonly HashSet<object> _watched = new(ReferenceEqualityComparer.Instance);

    public ConfigAutoSaver(DispatcherQueue queue, Action save)
    {
        _queue = queue;
        _timer = queue.CreateTimer();
        _timer.Interval = Delay;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => save();
    }

    /// <summary>Starts watching everything reachable from <paramref name="root"/> that is not watched yet.</summary>
    public void Watch(object? root)
    {
        if (root == null || root is string || root.GetType().IsValueType || !_watched.Add(root))
            return;

        if (root is INotifyPropertyChanged notifier)
            notifier.PropertyChanged += OnPropertyChanged;
        if (root is INotifyCollectionChanged collection)
            collection.CollectionChanged += OnCollectionChanged;

        if (root is IEnumerable items)
        {
            foreach (object? item in items)
                Watch(item);
            return;
        }

        if (root.GetType().Namespace?.StartsWith("Leyline", StringComparison.Ordinal) != true)
            return;

        foreach (var property in root.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType.IsValueType || property.PropertyType == typeof(string) ||
                property.GetIndexParameters().Length > 0 || GetSavedProperty(root.GetType(), property.Name) == null)
                continue;
            Watch(property.GetValue(root));
        }
    }

    /// <summary>Saves after <see cref="Delay"/>, restarting the wait on each call (a slider drag saves once).</summary>
    public void ScheduleSave()
    {
        if (!_queue.HasThreadAccess)
        {
            _queue.TryEnqueue(ScheduleSave);
            return;
        }
        _timer.Stop();
        _timer.Start();
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == null || string.IsNullOrEmpty(e.PropertyName))
            return;
        var property = GetSavedProperty(sender.GetType(), e.PropertyName);
        if (property == null)
            return;

        if (!_queue.HasThreadAccess)
        {
            _queue.TryEnqueue(() => OnSavedPropertyChanged(sender, property));
            return;
        }
        OnSavedPropertyChanged(sender, property);
    }

    private void OnSavedPropertyChanged(object sender, PropertyInfo property)
    {
        // A replaced child (a new effect chain, a new list) needs watching too
        if (!property.PropertyType.IsValueType && property.PropertyType != typeof(string))
            Watch(property.GetValue(sender));
        ScheduleSave();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_queue.HasThreadAccess)
        {
            _queue.TryEnqueue(() => OnCollectionChanged(sender, e));
            return;
        }
        if (e.NewItems != null)
        {
            foreach (object? item in e.NewItems)
                Watch(item);
        }
        ScheduleSave();
    }

    // A property that goes into config.json: public, readable, not [JsonIgnore]
    private static PropertyInfo? GetSavedProperty(Type type, string name) =>
        SavedProperties.GetOrAdd((type, name), key =>
        {
            var property = key.Item1.GetProperty(key.Item2, BindingFlags.Public | BindingFlags.Instance);
            return property != null && property.CanRead && property.GetIndexParameters().Length == 0 &&
                   property.GetCustomAttribute<JsonIgnoreAttribute>() == null
                ? property
                : null;
        });
}
