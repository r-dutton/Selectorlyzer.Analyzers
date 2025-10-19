using System;
using System.Collections.Generic;

namespace Selectorlyzer.Qulaly.Matcher
{
    public sealed class SelectorMatchState
    {
        private readonly SelectorMatchState? _parent;
        private Dictionary<string, object?>? _captures;
        private IReadOnlyDictionary<string, object?>? _cachedAggregate;

        public SelectorMatchState()
        {
        }

        private SelectorMatchState(SelectorMatchState parent)
        {
            _parent = parent;
        }

        public IReadOnlyDictionary<string, object?>? Captures
        {
            get
            {
                if (_captures is null)
                {
                    return _parent?.Captures;
                }

                if (_parent is null)
                {
                    return _captures;
                }

                if (_cachedAggregate is null)
                {
                    var aggregate = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                    if (_parent.Captures is { } parentCaptures)
                    {
                        foreach (var kvp in parentCaptures)
                        {
                            aggregate[kvp.Key] = kvp.Value;
                        }
                    }

                    foreach (var kvp in _captures)
                    {
                        aggregate[kvp.Key] = kvp.Value;
                    }

                    _cachedAggregate = aggregate;
                }

                return _cachedAggregate;
            }
        }

        public void SetCapture(string name, object? value)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            _captures ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            _captures[name] = value;
            _cachedAggregate = null;
        }

        public bool TryGetCapture(string name, out object? value)
        {
            if (!string.IsNullOrWhiteSpace(name) && _captures is not null && _captures.TryGetValue(name, out value))
            {
                return true;
            }

            if (_parent is not null)
            {
                return _parent.TryGetCapture(name, out value);
            }

            value = null;
            return false;
        }

        public SelectorMatchState CreateChild()
        {
            return new SelectorMatchState(this);
        }
    }
}
