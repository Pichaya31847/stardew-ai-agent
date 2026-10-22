using System;
using System.Collections.Generic;
using Netcode;
using StardewMCP.Agent;
using Xunit;

namespace StardewMCP.Tests;

public class PeekTests
{
    private class FakeBase
    {
        public int PublicBaseField = 10;
        private string PrivateBaseField = "base_private";
        public virtual string VirtualProp { get; set; } = "base_prop";
        public string OverriddenField = "base_overridden";

        public int BaseMethod() => 42;

        public string GetPrivateBaseFieldForCompiler() => PrivateBaseField; // to prevent CS0414
    }

    private class FakeDerived : FakeBase
    {
        public int PublicField = 20;
        private string PrivateField = "derived_private";

        public string PublicProperty { get; set; } = "prop_value";
        private string PrivateProperty { get; set; } = "private_prop_value";

        public string GetPrivateFieldForCompiler() => PrivateField; // to prevent CS0414

        public override string VirtualProp { get; set; } = "derived_prop";
        public new string OverriddenField = "derived_overridden";

        public static int StaticField = 100;
        public static string StaticProperty { get; set; } = "static_prop";

        public string WriteOnlyProperty { set { } }

        public string this[int index] => "indexer_value";

        public string ParameterlessMethod() => "method_result";
        public string MethodWithArgs(string arg) => arg;

        public string ThrowingProperty => throw new InvalidOperationException("Oops");
        public string ThrowingMethod() => throw new InvalidOperationException("Oops");

        // Netcode types
        public NetString WrappedString = new NetString("net_string_value");
        public NetRef<NetString> DoubleWrapped = new NetRef<NetString>(new NetString("double_wrapped"));
        public NetRef<NetRef<NetString>> TripleWrapped = new NetRef<NetRef<NetString>>(new NetRef<NetString>(new NetString("triple_wrapped")));

        // Unwrap limits to 3 times, so a 4-deep wrap will only unwrap 3 levels
        public NetRef<NetRef<NetRef<NetString>>> QuadWrapped = new NetRef<NetRef<NetRef<NetString>>>(new NetRef<NetRef<NetString>>(new NetRef<NetString>(new NetString("quad_wrapped"))));

        public NetCollection<string> Collection = new NetCollection<string> { "item1", "item2" };

        public long LongValue = 10000000000L; // > int.MaxValue
        public uint UintValue = 4000000000u; // > int.MaxValue
        public float FloatValue = 42.9f;
        public double DoubleValue = 3.14159;
    }

    [Fact]
    public void Get_FieldsAndProperties_DeclaredAndInherited()
    {
        var target = new FakeDerived();

        // Declared public field
        Assert.Equal(20, Peek.Get(target, "PublicField"));

        // Declared private field
        Assert.Equal("derived_private", Peek.Get(target, "PrivateField"));

        // Declared public property
        Assert.Equal("prop_value", Peek.Get(target, "PublicProperty"));

        // Declared private property
        Assert.Equal("private_prop_value", Peek.Get(target, "PrivateProperty"));

        // Inherited public field
        Assert.Equal(10, Peek.Get(target, "PublicBaseField"));

        // Inherited private field (from base class)
        Assert.Equal("base_private", Peek.Get(target, "PrivateBaseField"));

        // Inherited virtual property (overridden in derived)
        Assert.Equal("derived_prop", Peek.Get(target, "VirtualProp"));

        // Hiding a base field (new keyword)
        Assert.Equal("derived_overridden", Peek.Get(target, "OverriddenField"));
    }

    [Fact]
    public void Get_Methods()
    {
        var target = new FakeDerived();

        // Method without arguments
        Assert.Equal("method_result", Peek.Get(target, "ParameterlessMethod()"));

        // Inherited method without arguments
        Assert.Equal(42, Peek.Get(target, "BaseMethod()"));

        // Method with arguments should read as not there (null) when called with ()
        Assert.Null(Peek.Get(target, "MethodWithArgs()"));
        // A "Name()" method that takes parameters is not called (reads as null)
        Assert.Null(Peek.Get(target, "MethodWithArgs"));
    }

    [Fact]
    public void GetStatic_ReadsStaticMembers()
    {
        Assert.Equal(100, Peek.GetStatic(typeof(FakeDerived), "StaticField"));
        Assert.Equal("static_prop", Peek.GetStatic(typeof(FakeDerived), "StaticProperty"));
    }

    [Fact]
    public void Get_IndexerOrWriteOnlyProperty_ReturnsNull()
    {
        var target = new FakeDerived();

        // Write-only property reads as null (p.CanRead is false)
        Assert.Null(Peek.Get(target, "WriteOnlyProperty"));

        // Indexer (has parameters) reads as null
        Assert.Null(Peek.Get(target, "Item"));
    }

    [Fact]
    public void Get_MissingOrThrowingMember_ReturnsNull()
    {
        var target = new FakeDerived();

        Assert.Null(Peek.Get(target, "NonExistentMember"));
        Assert.Null(Peek.Get(target, "ThrowingProperty"));
        Assert.Null(Peek.Get(target, "ThrowingMethod()"));
        Assert.Null(Peek.Get(target, "NonExistentMethod()"));
    }

    [Fact]
    public void Get_NetcodeWrappers_UnwrapsToValue()
    {
        var target = new FakeDerived();

        Assert.Equal("net_string_value", Peek.Get(target, "WrappedString"));
        Assert.Equal("double_wrapped", Peek.Get(target, "DoubleWrapped"));
        Assert.Equal("triple_wrapped", Peek.Get(target, "TripleWrapped"));

        // Max 3 iterations, so 4th level is returned as NetString
        var quadResult = Peek.Get(target, "QuadWrapped");
        Assert.IsType<NetString>(quadResult);
        Assert.Equal("quad_wrapped", ((NetString)quadResult).Value);

        // Collection without Value property returns as is
        var collectionResult = Peek.Get(target, "Collection");
        Assert.IsType<NetCollection<string>>(collectionResult);
    }

    [Fact]
    public void Text_ReturnsFirstNonEmptyTrimmedString()
    {
        var target = new FakeDerived();
        target.PublicProperty = "  "; // Empty after trim
        // Use a property we can modify for the test
        target.OverriddenField = "   valid_text  ";

        var result = Peek.Text(target, "MissingProp", "PublicProperty", "OverriddenField", "VirtualProp");
        Assert.Equal("valid_text", result);

        // All empty or missing
        Assert.Equal("", Peek.Text(target, "MissingProp", "PublicProperty"));
    }

    [Fact]
    public void Int_ConvertsAndClampsNumerics()
    {
        var target = new FakeDerived();

        Assert.Equal(20, Peek.Int(target, "PublicField")); // int

        // long clamped to int max
        Assert.Equal(int.MaxValue, Peek.Int(target, "LongValue"));

        target.LongValue = -10000000000L;
        Assert.Equal(int.MinValue, Peek.Int(target, "LongValue"));

        // uint > int.MaxValue clamped to int.MaxValue
        Assert.Equal(int.MaxValue, Peek.Int(target, "UintValue"));

        target.UintValue = 100u;
        Assert.Equal(100, Peek.Int(target, "UintValue"));

        // float truncated to int
        Assert.Equal(42, Peek.Int(target, "FloatValue"));

        // double truncated to int
        Assert.Equal(3, Peek.Int(target, "DoubleValue"));

        // null when none found
        Assert.Null(Peek.Int(target, "MissingProp", "PublicProperty"));
    }

    [Fact]
    public void Flag_ReturnsFirstBool()
    {
        var target = new { Flag1 = "not_bool", Flag2 = true, Flag3 = false };

        Assert.True(Peek.Flag(target, "Flag1", "Flag2"));
        Assert.False(Peek.Flag(target, "Flag3"));
        Assert.Null(Peek.Flag(target, "Flag1", "MissingFlag"));
    }

    [Fact]
    public void Items_ReturnsCollectionElements()
    {
        var target = new FakeDerived();

        var items = Peek.Items(Peek.Get(target, "Collection"));
        Assert.Equal(2, items.Count);
        Assert.Equal("item1", items[0]);
        Assert.Equal("item2", items[1]);

        // String is not a list
        var stringItems = Peek.Items("this is a string");
        Assert.Empty(stringItems);

        // Missing or non-enumerable
        Assert.Empty(Peek.Items(null));
        Assert.Empty(Peek.Items(42));

        // Skips null elements
        var listWithNulls = new List<object?> { "item1", null, "item2" };
        var listItems = Peek.Items(listWithNulls);
        Assert.Equal(2, listItems.Count);
    }

    [Fact]
    public void Clean_ReplacesCarrotsAndCollapsesWhitespace()
    {
        Assert.Equal("", Peek.Clean(null));
        Assert.Equal("", Peek.Clean("   "));
        Assert.Equal("Hello World", Peek.Clean("Hello^World"));
        Assert.Equal("A B C", Peek.Clean("A   B \t C"));
        Assert.Equal("Line 1 Line 2", Peek.Clean(" Line 1 ^  Line 2 "));
    }

    [Fact]
    public void Clip_TruncatesLongStringsWithEllipsis()
    {
        Assert.Equal("short", Peek.Clip("short", 10));
        Assert.Equal("exact_10__", Peek.Clip("exact_10__", 10));
        Assert.Equal("long...", Peek.Clip("long_string", 7));
        Assert.Equal("...", Peek.Clip("toolong", 3)); // Max 3 length test
        Assert.Equal("...", Peek.Clip("toolong", 2)); // Edge case: length < 3
    }
}
