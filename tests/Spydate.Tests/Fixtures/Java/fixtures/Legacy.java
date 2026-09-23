package fixtures;

import java.util.concurrent.TimeUnit;

/**
 * What javac writes when it compiles for Java 8, before nestmates: an inner class reaches its outer class's
 * private members through synthetic {@code access$000} methods, and a switch on an enum from another class goes
 * through a {@code $SwitchMap$} array in a synthetic class. Compiled with {@code --release 8}; {@code main}
 * prints what everything returns, so a decompiled and recompiled copy can be checked against the original.
 */
public class Legacy {
    private int secret = 5;
    private String name = "legacy";

    private int twice(int x) {
        return x * 2;
    }

    public class Peek {
        private final int bonus;

        public Peek(int bonus) {
            this.bonus = bonus;
        }

        public int read() {
            return secret + bonus;
        }

        public void write(int value) {
            secret = value;
        }

        public int doubled() {
            return twice(secret);
        }

        public String label() {
            return name.toUpperCase();
        }
    }

    // Private constructors a nested class calls: javac 8 adds one taking an extra null of a class of its own.
    private static class Secret {
        private final int value;

        private Secret(int value) {
            this.value = value;
        }
    }

    private static class Base {
        protected final String tag;

        private Base(String tag) {
            this.tag = tag;
        }
    }

    private static class Derived extends Base {
        Derived() {
            super("derived");
        }
    }

    public static String unit(TimeUnit unit) {
        switch (unit) {
            case SECONDS:
                return "s";
            case MINUTES:
                return "min";
            default:
                return unit.name().toLowerCase();
        }
    }

    public int run() {
        Peek peek = new Peek(10);
        int first = peek.read();
        peek.write(7);
        return first * 100 + peek.read() * 10 + peek.doubled();
    }

    public static void main(String[] args) {
        Legacy legacy = new Legacy();
        Legacy.Peek other = legacy.new Peek(1);
        StringBuilder out = new StringBuilder();
        out.append(legacy.run()).append(' ').append(other.read()).append(' ').append(other.label()).append('\n');
        out.append(unit(TimeUnit.SECONDS)).append(unit(TimeUnit.MINUTES)).append(unit(TimeUnit.HOURS)).append('\n');
        out.append(new Secret(42).value).append(' ').append(new Derived().tag).append('\n');
        System.out.print(out);
    }
}
