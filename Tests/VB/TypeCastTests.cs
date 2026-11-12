using System.Threading.Tasks;
using ICSharpCode.CodeConverter.Tests.TestRunners;
using Xunit;

namespace ICSharpCode.CodeConverter.Tests.VB;

public class TypeCastTests : ConverterTestBase
{
    [Fact]
    public async Task CastObjectToIntegerAsync()
    {
        // The leading and trailing newlines check that surrounding trivia is selected as part of this (in the comments auto-testing)
        await TestConversionCSharpToVisualBasicAsync(
            @"
void Test()
{
    object o = 5;
    int i = (int) o;
}
", @"
Private Sub Test()
    Dim o As Object = 5
    Dim i As Integer = o
End Sub
");
    }

    [Fact]
    public async Task CastObjectToStringAsync()
    {
        await TestConversionCSharpToVisualBasicAsync(
            @"void Test()
{
    object o = ""Test"";
    string s = (string) o;
}", @"Private Sub Test()
    Dim o As Object = ""Test""
    Dim s = CStr(o)
End Sub");
    }

    [Fact]
    public async Task CastObjectToGenericListAsync()
    {
        await TestConversionCSharpToVisualBasicAsync(
            @"void Test()
{
    object o = new System.Collections.Generic.List<int>();
    System.Collections.Generic.List<int> l = (System.Collections.Generic.List<int>) o;
}", @"Private Sub Test()
    Dim o As Object = New List(Of Integer)()
    Dim l = CType(o, List(Of Integer))
End Sub");
    }

    [Fact]
    public async Task TryCastObjectToIntegerAsync()
    {
        await TestConversionCSharpToVisualBasicAsync(
            @"void Test()
{
    object o = 5;
    System.Nullable<int> i = o as int?;
}", @"Private Sub Test()
    Dim o As Object = 5
    Dim i As Integer? = o
End Sub");
    }

    [Fact]
    public async Task TryCastObjectToGenericListAsync()
    {
        await TestConversionCSharpToVisualBasicAsync(
            @"void Test()
{
    object o = new System.Collections.Generic.List<int>();
    System.Collections.Generic.List<int> l = o as System.Collections.Generic.List<int>;
}", @"Private Sub Test()
    Dim o As Object = New List(Of Integer)()
    Dim l As List(Of Integer) = TryCast(o, List(Of Integer))
End Sub");
    }

    [Fact]
    public async Task TryCastObjectToGenericTypeAsync() {
        await TestConversionCSharpToVisualBasicAsync(
            @"T Test<T>() where T : class {
    return this as T;
}
",
            @"Private Function Test(Of T As Class)() As T
    Return TryCast(Me, T)
End Function
");
    }

    [Fact]
    public async Task CastConstantNumberToLongAsync()
    {
        await TestConversionCSharpToVisualBasicAsync(
            @"void Test()
{
    object o = 5L;
}", @"Private Sub Test()
    Dim o As Object = 5L
End Sub");
    }

    [Fact]
    public async Task CastConstantNumberToFloatAsync()
    {
        await TestConversionCSharpToVisualBasicAsync(
            @"void Test()
{
    object o = 5.0f;
}", @"Private Sub Test()
    Dim o As Object = 5.0F
End Sub");
    }

    [Fact]
    public async Task CastConstantNumberToDecimalAsync()
    {
        await TestConversionCSharpToVisualBasicAsync(
            @"void Test()
{
    object o = 5.0m;
}", @"Private Sub Test()
    Dim o As Object = 5.0D
End Sub");
    }

    [Fact]
    public async Task CastConstantNumberToCharacterAsync()
    {
        await TestConversionCSharpToVisualBasicAsync(
            @"void Test() {
    char CR = (char)0xD;
}",
            @"Private Sub Test()
    Dim CR = Microsoft.VisualBasic.ChrW(&HD)
End Sub", conversionOptions: EmptyNamespaceOptionStrictOff);
    }
    [Fact]
    public async Task CastCharacterToNumberAsync() {
        await TestConversionCSharpToVisualBasicAsync(
            @"void Test() {
    byte a = (byte)'A';
    decimal b = (byte)'B';
}",
            @"Private Sub Test()
    Dim a As Byte = Microsoft.VisualBasic.AscW(""A""c)
    Dim b As Decimal = Microsoft.VisualBasic.AscW(""B""c)
End Sub", conversionOptions: EmptyNamespaceOptionStrictOff);
    }
    [Fact(Skip = "Many code generation")]
    public async Task CastCharacterIncrementAsync() {
        await TestConversionCSharpToVisualBasicAsync(
            @"void Test() {
    char a = 'A';
    a++;
}
",
            @"Private Sub Test()
    Dim a As Char = ""A""c
    a == ChrW(AscW(a) + 1)
End Sub");
    }
    [Fact]
    public async Task MethodInvocationAsync() {
        await TestConversionCSharpToVisualBasicAsync(
            @"public class Test {
    public void TestMethod() { }
}
public class Test2 {
    public void TestMethod(object o) {
        ((Test)o).TestMethod();
    }
}",
            @"Public Class Test
    Public Sub TestMethod()
    End Sub
End Class

Public Class Test2
    Public Sub TestMethod(o As Object)
        CType(o, Test).TestMethod()
    End Sub
End Class");
    }

    [Fact]
    public async Task MethodInvocation_TryCastAsync() {
        await TestConversionCSharpToVisualBasicAsync(
            @"public class Test {
    public void TestMethod() { }
}
public class Test2 {
    public void TestMethod(object o) {
        (o as Test).TestMethod();
    }
}",
            @"Public Class Test
    Public Sub TestMethod()
    End Sub
End Class

Public Class Test2
    Public Sub TestMethod(o As Object)
        TryCast(o, Test).TestMethod()
    End Sub
End Class");
    }

    [Fact]
    public async Task CastSelectingConstructorOverloadIsKeptAsync()
    {
        await TestConversionCSharpToVisualBasicAsync(
            @"public enum Img { None, Info }

public class P
{
    public string V;
    public P(Img i) { V = ""img""; }
    public P(string s) { V = ""str""; }

    public static string Create(object o)
    {
        return new P((Img)o).V;
    }
}",
            @"Public Enum Img
    None
    Info
End Enum

Public Class P
    Public V As String
    Public Sub New(i As Img)
        V = ""img""
    End Sub
    Public Sub New(s As String)
        V = ""str""
    End Sub

    Public Shared Function Create(o As Object) As String
        Return New P(CType(o, Img)).V
    End Function
End Class");
    }

    [Fact]
    public async Task CastSelectingMethodOverloadIsKeptAsync()
    {
        await TestConversionCSharpToVisualBasicAsync(
            @"public enum Img { None, Info }

public class P
{
    public static string Show(Img i) { return ""img""; }
    public static string Show(string s) { return ""str""; }

    public static string Test(object o)
    {
        return Show((Img)o);
    }
}",
            @"Public Enum Img
    None
    Info
End Enum

Public Class P
    Public Shared Function Show(i As Img) As String
        Return ""img""
    End Function
    Public Shared Function Show(s As String) As String
        Return ""str""
    End Function

    Public Shared Function Test(o As Object) As String
        Return Show(CType(o, Img))
    End Function
End Class");
    }

    [Fact]
    public async Task CastSelectingNumericConstructorOverloadIsKeptAsync()
    {
        await TestConversionCSharpToVisualBasicAsync(
            @"public class P
{
    public string V;
    public P(int i) { V = ""int""; }
    public P(long l) { V = ""long""; }

    public static string FromLong(long l) { return new P((int)l).V; }
    public static string FromInt(int i) { return new P((long)i).V; }
}",
            @"Public Class P
    Public V As String
    Public Sub New(i As Integer)
        V = ""int""
    End Sub
    Public Sub New(l As Long)
        V = ""long""
    End Sub

    Public Shared Function FromLong(l As Long) As String
        Return New P(CInt(l)).V
    End Function
    Public Shared Function FromInt(i As Integer) As String
        Return New P(CLng(i)).V
    End Function
End Class");
    }

    [Fact]
    public async Task CastNotSelectingAnOverloadIsStillRemovedAsync()
    {
        await TestConversionCSharpToVisualBasicAsync(
            @"public enum Img { None, Info }

public class S
{
    public S(int i) { }
    public static S Create(Img i) { return null; }
    public static S CreateFrom(object o) { return Create((Img)o); }
    public static S CreateNumbered(long l) { return new S((int)l); }
}",
            @"Public Enum Img
    None
    Info
End Enum

Public Class S
    Public Sub New(i As Integer)
    End Sub
    Public Shared Function Create(i As Img) As S
        Return Nothing
    End Function
    Public Shared Function CreateFrom(o As Object) As S
        Return Create(o)
    End Function
    Public Shared Function CreateNumbered(l As Long) As S
        Return New S(l)
    End Function
End Class");
    }

    [Fact]
    public async Task CastWithinConstructorArgumentSelectingOverloadIsKeptAsync()
    {
        await TestConversionCSharpToVisualBasicAsync(
            @"public class P
{
    public P(int i) { }
    public P(long l) { }

    public static P Conditional(bool b, long l) { return new P(b ? (int)l : 0); }
    public static P Parenthesized(long l) { return new P(((int)l)); }
}",
            @"Public Class P
    Public Sub New(i As Integer)
    End Sub
    Public Sub New(l As Long)
    End Sub

    Public Shared Function Conditional(b As Boolean, l As Long) As P
        Return New P(If(b, CInt(l), 0))
    End Function
    Public Shared Function Parenthesized(l As Long) As P
        Return New P(CInt(l))
    End Function
End Class");
    }

    [Fact]
    public async Task CastInLambdaSelectingConstructorOverloadIsKeptAsync()
    {
        await TestConversionCSharpToVisualBasicAsync(
            @"using System;

public class P
{
    public P(Func<int> f) { }
    public P(Func<long> f) { }

    public static P Expression(long l) { return new P(() => (int)l); }
    public static P Block(long l) { return new P(() => { Console.WriteLine(l); return (int)l; }); }
}

public class Q
{
    public Q(Func<long> f) { }

    public static Q Single(int i) { return new Q(() => (long)i); }
}",
            @"Imports System

Public Class P
    Public Sub New(f As Func(Of Integer))
    End Sub
    Public Sub New(f As Func(Of Long))
    End Sub

    Public Shared Function Expression(l As Long) As P
        Return New P(Function() CInt(l))
    End Function
    Public Shared Function Block(l As Long) As P
        Return New P(Function()
                         Console.WriteLine(l)
                         Return CInt(l)
                     End Function)
    End Function
End Class

Public Class Q
    Public Sub New(f As Func(Of Long))
    End Sub

    Public Shared Function [Single](i As Integer) As Q
        Return New Q(Function() i)
    End Function
End Class");
    }

    [Fact]
    public async Task CastInConditionalAccessCallNotSelectingAnOverloadIsStillRemovedAsync()
    {
        await TestConversionCSharpToVisualBasicAsync(
            @"public class C
{
    public void Show(int i) { }

    public void M(C c, object o, long l)
    {
        c?.Show((int)o);
        c?.Show((int)l);
    }
}",
            @"Public Class C
    Public Sub Show(i As Integer)
    End Sub

    Public Sub M(c As C, o As Object, l As Long)
        c?.Show(o)
        c?.Show(l)
    End Sub
End Class");
    }
}
