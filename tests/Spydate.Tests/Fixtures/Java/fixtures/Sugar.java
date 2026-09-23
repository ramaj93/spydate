package fixtures;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.StringReader;
import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.Callable;
import java.util.function.Function;
import java.util.function.IntBinaryOperator;
import java.util.function.IntFunction;
import java.util.function.Supplier;

/**
 * What javac writes for the language's shortcuts, and for classes inside classes: lambdas and method references,
 * for-each, switches on strings and enums, switch expressions, try-with-resources, assert, array initialisers,
 * enums, records, annotations, and inner, nested, anonymous and local classes with the fields and constructors
 * javac adds to them. {@code main} prints what each returns — the annotation is read back by reflection — so a
 * decompiled and recompiled copy can be checked against the original.
 */
public class Sugar {
    @Retention(RetentionPolicy.RUNTIME)
    @Target({ElementType.METHOD, ElementType.TYPE, ElementType.FIELD})
    public @interface Note {
        String value();

        int weight() default 1;
    }

    public enum Color {
        RED("r"),
        GREEN("g"),
        BLUE("b") {
            @Override
            public String describe() {
                return "deep " + name().toLowerCase();
            }
        };

        private final String code;

        Color(String code) {
            this.code = code;
        }

        public String code() {
            return code;
        }

        public String describe() {
            return name().toLowerCase();
        }
    }

    public record Point(int x, int y) {
        public Point {
            if (x < 0) {
                throw new IllegalArgumentException("x");
            }
        }

        public int sum() {
            return x + y;
        }
    }

    public static class Counter {
        private int count;

        public void add(int n) {
            count += n;
        }

        public int get() {
            return count;
        }
    }

    public class Tally {
        private int total;

        public void bump() {
            total += base;
        }

        public int total() {
            return total;
        }
    }

    private static final int[] PRIMES = {2, 3, 5, 7, 11};
    private static final Map<String, Integer> NAMES = new HashMap<>();

    // A field read before it is declared: Java only allows it qualified, even inside a lambda.
    private static final Supplier<String> LATER = () -> Sugar.TAIL.get(0);
    private static final List<String> TAIL = new ArrayList<>(Arrays.asList("tail"));

    // A member class named in its outer class's own header, where it is only in scope by the outer name.
    public static class Holder extends ArrayList<Holder.Item> {
        public static class Item {
            final int size;

            Item(int size) {
                this.size = size;
            }
        }

        int total() {
            int sum = 0;
            for (Item item : this) {
                sum += item.size;
            }
            return sum;
        }
    }
    private final int base;
    private final List<String> log = new ArrayList<>();

    static {
        NAMES.put("one", 1);
        NAMES.put("two", 2);
    }

    public Sugar(int base) {
        this.base = base;
    }

    @Note(value = "sums", weight = 3)
    public static int sumAll(int[] values) {
        int sum = 0;
        for (int v : values) {
            sum += v;
        }
        return sum;
    }

    public static String joinAll(List<String> words) {
        StringBuilder sb = new StringBuilder();
        for (String w : words) {
            if (sb.length() > 0) {
                sb.append(',');
            }
            sb.append(w);
        }
        return sb.toString();
    }

    public static int dayNumber(String day) {
        switch (day) {
            case "mon":
                return 1;
            case "tue":
                return 2;
            case "Aa":
            case "BB":
                return 99;
            default:
                return -1;
        }
    }

    public static String colorName(Color c) {
        switch (c) {
            case RED:
                return "red!";
            case GREEN:
                return "green!";
            default:
                return "other";
        }
    }

    public static int arrow(int k) {
        return switch (k) {
            case 1, 2 -> 10;
            case 3 -> {
                int t = k * 7;
                yield t + 1;
            }
            default -> -1;
        };
    }

    public static List<Integer> lambdas(List<Integer> in, int offset) {
        List<Integer> out = new ArrayList<>();
        in.forEach(x -> out.add(x + offset));
        out.sort(Comparator.reverseOrder());
        Function<Integer, String> f = String::valueOf;
        Supplier<List<String>> make = ArrayList::new;
        List<String> strings = make.get();
        for (Integer i : out) {
            strings.add(f.apply(i));
        }
        IntBinaryOperator op = (a, b) -> a * b + offset;
        out.add(op.applyAsInt(3, 4));
        out.add(strings.size());
        return out;
    }

    public static int readLines(String text) throws IOException {
        int n = 0;
        try (BufferedReader r = new BufferedReader(new StringReader(text))) {
            String line;
            while ((line = r.readLine()) != null) {
                if (line.isEmpty()) {
                    return -n;
                }
                n++;
            }
        }
        return n;
    }

    static int widen(byte b, short s) {
        return b * 100 + s;
    }

    // A boolean the bytecode compares as an int.
    static boolean outside(int c, int lo, int hi, boolean negated) {
        return (c >= lo && c <= hi) != negated;
    }

    static String pick(String s, Object o) {
        return "string " + o;
    }

    static String pick(Integer i, Object o) {
        return "integer " + o;
    }

    static String call(Supplier<String> s) {
        return "supplier " + s.get();
    }

    static String call(Callable<String> c) throws Exception {
        return "callable " + c.call();
    }

    static int count(String label, Object... items) {
        return label.length() + items.length;
    }

    // Arguments that only a cast or nothing at all makes the call the same one again.
    public static String overloads() throws Exception {
        IntFunction<String[]> make = String[]::new;
        Holder holder = new Holder();
        holder.add(new Holder.Item(2));
        holder.add(new Holder.Item(5));
        return widen((byte) 3, (short) 4) + " " + outside(5, 1, 9, false) + outside(5, 1, 9, true) + " " + pick((String) null, "x")
            + " " + call((Supplier<String>) () -> "s") + " " + count("none") + count("two", 1, 2) + " " + make.apply(3).length
            + " " + holder.total() + " " + LATER.get();
    }

    public static String guarded(int x) {
        assert x >= 0 : "negative";
        return "ok" + x;
    }

    public int useInner() {
        Tally t = new Tally();
        t.bump();
        t.bump();
        Counter c = new Counter();
        c.add(t.total());
        Runnable r = new Runnable() {
            @Override
            public void run() {
                log.add("ran " + base);
            }
        };
        r.run();
        int captured = base * 2;
        Supplier<Integer> s = new Supplier<Integer>() {
            @Override
            public Integer get() {
                return captured + 1;
            }
        };
        class Local {
            int twice() {
                return captured * 2;
            }
        }
        return c.get() + s.get() + new Local().twice() + log.size();
    }

    public static void main(String[] args) throws Exception {
        StringBuilder out = new StringBuilder();
        out.append(sumAll(PRIMES)).append(' ').append(sumAll(new int[] {1, 2, 3})).append('\n');
        out.append(joinAll(Arrays.asList("a", "b", "c"))).append('\n');
        for (String d : new String[] {"mon", "tue", "Aa", "BB", "sun"}) {
            out.append(dayNumber(d)).append(',');
        }
        out.append('\n');
        for (Color c : Color.values()) {
            out.append(colorName(c)).append(c.code()).append(c.describe()).append(c.ordinal());
        }
        out.append('\n');
        for (int k = 0; k <= 4; k++) {
            out.append(arrow(k)).append(',');
        }
        out.append('\n');
        out.append(lambdas(Arrays.asList(1, 2, 3), 10)).append('\n');
        out.append(readLines("a\nb\nc")).append(readLines("a\n\nc")).append('\n');
        out.append(guarded(5)).append('\n');
        out.append(new Point(1, 2)).append(new Point(3, 4).sum()).append(new Point(1, 2).equals(new Point(1, 2))).append('\n');
        out.append(new Sugar(7).useInner()).append(NAMES.get("two")).append('\n');
        try {
            new Point(-1, 0);
        } catch (IllegalArgumentException e) {
            out.append("rejected ").append(e.getMessage()).append('\n');
        }
        out.append(Sugar.class.getMethod("sumAll", int[].class).getAnnotation(Note.class).weight()).append('\n');
        out.append(overloads()).append('\n');
        System.out.print(out);
    }
}
