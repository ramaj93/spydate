package fixtures;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.Supplier;

/**
 * What erasure takes out of the class file and the source had to say: casts to type variables, which leave no
 * bytecode or only a cast to the bound's erasure; locals of type {@code T}, which without a local variable type
 * table are only {@code Object}; generic methods inherited through a parameterized superclass; explicit type
 * arguments on a call javac cannot infer; multi-catch, which the table lists as one handler per type. {@code main}
 * prints what everything returns, so a decompiled and recompiled copy can be checked against the original.
 */
public class Generics {
    // A value of T that is a sentinel Object: the source's (T) cast leaves nothing in the bytecode.
    public static class Lazy<T> {
        private static final Object NO_VALUE = new Object();
        private final Supplier<T> supplier;
        private final AtomicReference<T> value = new AtomicReference<>(noValue());

        public Lazy(Supplier<T> supplier) {
            this.supplier = supplier;
        }

        @SuppressWarnings("unchecked")
        private T noValue() {
            return (T) NO_VALUE;
        }

        public T get() {
            T result = value.get();
            if (result == noValue()) {
                result = supplier.get();
                if (!value.compareAndSet(noValue(), result)) {
                    result = value.get();
                }
            }
            return result;
        }
    }

    // Type variables a subclass maps: Named's T is Base's T, its E is IllegalStateException.
    public abstract static class Base<T, E extends Exception> {
        protected abstract T make() throws E;

        protected abstract E wrap(Exception e);

        @SuppressWarnings("unchecked")
        public T build() throws E {
            try {
                return make();
            } catch (Exception e) {
                E typed = wrap(e);
                if (typed.getClass().isInstance(e)) {
                    throw (E) e;
                }
                throw typed;
            }
        }
    }

    public static class Named<T> extends Base<T, IllegalStateException> {
        private final T seed;

        public Named(T seed) {
            this.seed = seed;
        }

        @Override
        protected T make() {
            return seed;
        }

        @Override
        protected IllegalStateException wrap(Exception e) {
            return new IllegalStateException(e);
        }

        public List<T> twice() {
            T first = build();
            List<T> out = new ArrayList<>();
            out.add(first);
            out.add(make());
            return out;
        }
    }

    // A builder whose first call infers nothing where the chain's value goes: Box.<String>create().
    public static class Box<T> {
        private final List<T> items = new ArrayList<>();

        public static <T> Box<T> create() {
            return new Box<>();
        }

        public Box<T> add(T item) {
            items.add(item);
            return this;
        }

        public List<T> items() {
            return items;
        }
    }

    static final List<String> WORDS = Box.<String>create().add("x").add("y").items();

    public static class Pair<A, B> {
        final A first;
        final B second;

        Pair(A first, B second) {
            this.first = first;
            this.second = second;
        }

        Pair<B, A> swap() {
            return new Pair<>(second, first);
        }

        // Fields read through another instance: their type variables are that instance's arguments.
        static <A extends Comparable<A>, B> A larger(Pair<A, B> p, Pair<A, B> q) {
            A a = p.first.compareTo(q.first) > 0 ? p.first : q.first;
            return a;
        }

        @Override
        public String toString() {
            return first + "/" + second;
        }
    }

    @SafeVarargs
    public static <T> T[] append(T[] array, T... more) {
        T[] joined = Arrays.copyOf(array, array.length + more.length);
        System.arraycopy(more, 0, joined, array.length, more.length);
        return joined;
    }

    public static <T extends Comparable<T>> T max(List<T> items) {
        T best = null;
        for (T item : items) {
            if (best == null || item.compareTo(best) > 0) {
                best = item;
            }
        }
        return best;
    }

    @SuppressWarnings("unchecked")
    static <T> Class<T> classOf(T value) {
        return value == null ? null : (Class<T>) value.getClass();
    }

    static String parse(String s) {
        try {
            return String.valueOf(Integer.parseInt(s) / (s.length() - 1));
        } catch (NumberFormatException | ArithmeticException e) {
            return e.getClass().getSimpleName();
        }
    }

    public static void main(String[] args) {
        StringBuilder out = new StringBuilder();
        Lazy<String> lazy = new Lazy<>(() -> "made");
        out.append(lazy.get()).append(' ').append(lazy.get()).append('\n');
        out.append(new Named<>("n").twice()).append('\n');
        out.append(WORDS).append('\n');
        Pair<String, Integer> p = new Pair<>("b", 1);
        out.append(p.swap()).append(' ').append(Pair.larger(p, new Pair<>("a", 2))).append('\n');
        out.append(Arrays.toString(append(new String[] {"a"}, "b", "c"))).append(' ').append(max(Arrays.asList(3, 9, 4))).append('\n');
        out.append(classOf("s").getSimpleName()).append(' ').append(classOf(null)).append('\n');
        out.append(parse("12")).append(' ').append(parse("x")).append(' ').append(parse("1")).append('\n');
        System.out.print(out);
    }
}
