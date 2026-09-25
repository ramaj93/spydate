package fixtures;

import java.util.Arrays;
import java.util.List;
import java.util.function.Function;

/**
 * What javac 21 makes of pattern matching in switches — a loop around a {@code typeSwitch} or {@code enumSwitch} call
 * site whose labels are its static arguments, a restart index a failed guard sets — and the source it was: type
 * patterns with {@code when} guards, {@code case null}, sealed hierarchies switched over without a default, record
 * patterns flat and nested, record cases javac merges into a switch on a component, constants and enum constants
 * among patterns, the colon form in a loop, and pattern switches in a lambda. {@code main} prints what each returns,
 * so a decompiled and recompiled copy can be checked against the original.
 */
public class Patterns {
    sealed interface Shape permits Circle, Square, Rect {}

    record Circle(double r) implements Shape {}

    record Square(double side) implements Shape {}

    record Rect(double w, double h) implements Shape {}

    sealed interface Node permits Leaf, Pair {}

    record Leaf(int value) implements Node {}

    record Pair(Node left, Node right) implements Node {}

    record Box<T>(T content) {}

    enum Color { RED, GREEN, BLUE }

    static String kind(Object o) {
        return switch (o) {
            case Integer i when i > 3 -> "big " + i;
            case Integer i -> "int " + i;
            case String s -> "text " + s;
            default -> "other";
        };
    }

    static String nullable(Object o) {
        switch (o) {
            case null -> {
                return "null";
            }
            case String s -> {
                return "s" + s.length();
            }
            default -> {
                return "d";
            }
        }
    }

    static double area(Shape s) {
        return switch (s) {
            case Circle c -> Math.PI * c.r() * c.r();
            case Square q -> q.side() * q.side();
            case Rect(double w, double h) -> w * h;
        };
    }

    static String statement(Object o) {
        String r;
        switch (o) {
            case Long l:
                r = "long" + l;
                break;
            case CharSequence cs when cs.length() == 0:
                r = "empty";
                break;
            default:
                r = "?";
        }
        return r;
    }

    static int colors(Color c) {
        return switch (c) {
            case RED -> 1;
            case Color x when x.ordinal() > 1 -> 3;
            case Color x -> 2;
        };
    }

    static int sum(Node n) {
        return switch (n) {
            case Leaf(int v) -> v;
            case Pair(Leaf(int a), Leaf(int b)) -> a + b;
            case Pair(Node l, Node r) -> sum(l) + sum(r);
        };
    }

    static String guardedRecord(Object o) {
        return switch (o) {
            case Leaf(int v) when v > 10 -> "large leaf";
            case Leaf(int v) -> "leaf " + v;
            case Box<?>(String s) -> "box of text " + s;
            case Box<?> b -> "box " + b.content();
            case null, default -> "something";
        };
    }

    static String strings(String s) {
        return switch (s) {
            case "a" -> "letter a";
            case String t when t.length() > 3 -> "long";
            case String t -> "short " + t;
        };
    }

    static int loop(List<Object> items) {
        int total = 0;
        for (Object item : items) {
            switch (item) {
                case Integer i when i < 0:
                    continue;
                case Integer i:
                    total += i;
                    break;
                case String str:
                    if (str.isEmpty()) {
                        return -1;
                    }
                    total += str.length();
                    break;
                default:
                    break;
            }
        }
        return total;
    }

    static String unused(Object o) {
        int i = 7;
        String answer = switch (o) {
            case Integer x -> "int";
            case Long l -> "long " + i;
            case Object any -> "object";
        };
        for (int k = 0; k < 2; k++) {
            i += k;
        }
        return answer + i;
    }

    static Function<Object, String> lambda() {
        return o -> switch (o) {
            case Character c when Character.isDigit(c) -> "digit";
            case Character c -> "char " + c;
            default -> "?";
        };
    }

    static String twice(Object a, Object b) {
        String first = switch (a) {
            case Integer i -> "i" + i;
            default -> "-";
        };
        String second = switch (b) {
            case Integer i when i == 0 -> "zero";
            case Integer i -> "j" + i;
            default -> "-";
        };
        return first + second;
    }

    public static void main(String[] args) {
        StringBuilder out = new StringBuilder();
        for (Object o : new Object[] {5, 1, "x", 2.0}) {
            out.append(kind(o)).append(',');
        }
        out.append(nullable(null)).append(nullable("ab")).append(nullable(3)).append('\n');
        out.append(area(new Circle(1))).append(' ').append(area(new Square(2))).append(' ').append(area(new Rect(2, 3))).append('\n');
        out.append(statement(7L)).append(statement("")).append(statement("a")).append(statement(1)).append('\n');
        for (Color c : Color.values()) {
            out.append(colors(c));
        }
        out.append('\n');
        out.append(sum(new Pair(new Leaf(1), new Leaf(2)))).append(' ').append(sum(new Pair(new Pair(new Leaf(1), new Leaf(2)), new Leaf(3)))).append(' ').append(sum(new Leaf(9))).append('\n');
        for (Object o : new Object[] {new Leaf(11), new Leaf(2), new Box<>("x"), new Box<>(4), null, 5}) {
            out.append(guardedRecord(o)).append(',');
        }
        out.append('\n').append(strings("a")).append(strings("abcd")).append(strings("ab")).append('\n');
        out.append(loop(Arrays.asList(1, -5, "abc", 2.0, 4))).append(' ').append(loop(Arrays.asList(1, ""))).append('\n');
        out.append(unused(1)).append(unused(2L)).append(unused("s")).append('\n');
        out.append(lambda().apply('4')).append(lambda().apply('x')).append(lambda().apply(3)).append('\n');
        out.append(twice(1, 0)).append(twice("x", 5)).append('\n');
        try {
            kind(null);
            out.append("no NPE");
        } catch (NullPointerException expected) {
            out.append("NPE");
        }
        System.out.print(out);
    }
}
