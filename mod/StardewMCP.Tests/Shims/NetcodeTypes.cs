// Fakes of the game's Netcode wrappers for PeekTests. Peek unwraps any type in the "Netcode" namespace that has
// a Value property, so these only need the namespace and Value; they are not copies of the real classes.
namespace Netcode
{
    public class NetString
    {
        public string? Value { get; set; }

        public NetString(string? value)
        {
            Value = value;
        }
    }

    public class NetRef<T>
    {
        public T? Value { get; set; }

        public NetRef(T? value)
        {
            Value = value;
        }
    }

    public class NetCollection<T> : System.Collections.Generic.List<T>
    {
        // A Netcode type without a Value property, should be returned as is.
    }
}
