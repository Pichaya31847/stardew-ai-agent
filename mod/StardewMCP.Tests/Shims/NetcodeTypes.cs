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

    public class NetInt
    {
        public int Value { get; set; }

        public NetInt(int value)
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
