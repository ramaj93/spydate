package fixtures;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Map;

/**
 * Control flow and types javac compiles into shapes a decompiler must undo: joins that are not a branch's
 * post-dominator, exits from nested loops, try/finally, synchronized, switches with fall-through, conditional
 * expressions, and booleans and chars that the bytecode keeps as ints. {@code main} prints what every method
 * returns for a spread of inputs, so a decompiled and recompiled copy can be checked against the original.
 */
public class Shapes {
    private static final Object LOCK = new Object();
    private static int counter;

    public static int sumUntil(int[] values, int stop) {
        int sum = 0;
        for (int i = 0; i < values.length; i++) {
            if (values[i] == stop) {
                break;
            }
            if (values[i] < 0) {
                continue;
            }
            sum += values[i];
        }
        return sum;
    }

    public static int findPair(int[][] grid, int target) {
        int found = -1;
        outer:
        for (int r = 0; r < grid.length; r++) {
            for (int c = 0; c < grid[r].length; c++) {
                if (grid[r][c] == target) {
                    found = r * 100 + c;
                    break outer;
                }
                if (grid[r][c] < 0) {
                    continue outer;
                }
            }
        }
        return found;
    }

    public static int digits(int n) {
        int count = 0;
        do {
            count++;
            n /= 10;
        } while (n != 0);
        return count;
    }

    public static int collatz(long n) {
        int steps = 0;
        while (true) {
            if (n == 1) {
                return steps;
            }
            n = (n % 2 == 0) ? n / 2 : 3 * n + 1;
            steps++;
            if (steps > 1000) {
                break;
            }
        }
        return -1;
    }

    public static String classify(int a, int b, boolean strict) {
        String result;
        if (a > b) {
            if (strict && a - b > 10) {
                result = "much greater";
            } else {
                result = "greater";
            }
        } else if (a == b) {
            result = "equal";
        } else {
            result = strict ? "less (strict)" : "less";
        }
        return result;
    }

    public static boolean inRange(int x, int lo, int hi) {
        return x >= lo && x <= hi;
    }

    public static boolean anyNegative(int[] values) {
        boolean seen = false;
        for (int v : values) {
            seen |= v < 0;
        }
        return seen;
    }

    public static int countVowels(String text) {
        int count = 0;
        for (int i = 0; i < text.length(); i++) {
            char c = Character.toLowerCase(text.charAt(i));
            if (c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u') {
                count++;
            }
        }
        return count;
    }

    public static char grade(int score) {
        char g;
        if (score >= 90) {
            g = 'A';
        } else if (score >= 80) {
            g = 'B';
        } else {
            g = score >= 70 ? 'C' : 'F';
        }
        return g;
    }

    public static String shout(String text) {
        char[] chars = text.toCharArray();
        for (int i = 0; i < chars.length; i++) {
            char c = chars[i];
            if (c >= 'a' && c <= 'z') {
                chars[i] = (char) (c - 32);
            }
        }
        return new String(chars);
    }

    public static int checksum(byte[] data) {
        short acc = 0;
        for (byte b : data) {
            acc = (short) (acc * 31 + b);
        }
        return acc;
    }

    public static int fallThrough(int k) {
        int r = 0;
        switch (k) {
            case 1:
                r += 1;
            case 2:
                r += 2;
                break;
            case 3:
                return 30;
            case 4:
            case 5:
                r = 45;
                break;
            default:
                r = -1;
        }
        return r;
    }

    public static int switchInLoop(int[] ops) {
        int acc = 0;
        for (int i = 0; i < ops.length; i++) {
            switch (ops[i]) {
                case 0:
                    continue;
                case 1:
                    acc++;
                    break;
                case 2:
                    acc *= 2;
                    break;
                case 9:
                    return -acc;
                default:
                    acc -= ops[i];
            }
            acc += 1000;
        }
        return acc;
    }

    public static int parseOr(String s, int fallback) {
        try {
            return Integer.parseInt(s.trim());
        } catch (NumberFormatException e) {
            return fallback;
        } catch (NullPointerException e) {
            return -fallback;
        }
    }

    public static int withFinally(int x) {
        int r = 0;
        try {
            if (x < 0) {
                return -1;
            }
            r = 100 / x;
        } catch (ArithmeticException e) {
            r = -2;
        } finally {
            counter++;
        }
        return r;
    }

    public static int retryLoop(String[] inputs) {
        int ok = 0;
        for (int i = 0; i < inputs.length; i++) {
            try {
                if (inputs[i] == null) {
                    continue;
                }
                if (inputs[i].isEmpty()) {
                    break;
                }
                ok += Integer.parseInt(inputs[i]);
            } catch (NumberFormatException e) {
                ok -= 1;
            }
        }
        return ok;
    }

    public static int locked(int x) {
        synchronized (LOCK) {
            if (x > 5) {
                return x * 2;
            }
            counter += x;
        }
        return counter;
    }

    public static int nestedTernary(int a, int b, int c) {
        return a > b ? (a > c ? a : c) : (b > c ? b : c);
    }

    public static boolean xorLike(boolean p, boolean q) {
        boolean r = p != q;
        if (!r && p) {
            return false;
        }
        return r || (p && q);
    }

    public static int earlyExits(int[] values) {
        if (values == null) {
            return -1;
        }
        int best = Integer.MIN_VALUE;
        for (int v : values) {
            if (v == 0) {
                return 0;
            }
            if (v > best) {
                best = v;
            }
        }
        if (best < 0) {
            best = -best;
        }
        return best;
    }

    public static String joinWords(List<String> words) {
        StringBuilder sb = new StringBuilder();
        Iterator<String> it = words.iterator();
        while (it.hasNext()) {
            String w = it.next();
            if (w.isEmpty()) {
                continue;
            }
            if (sb.length() > 0) {
                sb.append(' ');
            }
            sb.append(w);
        }
        return sb.toString();
    }

    public static int histogram(String text) {
        Map<Character, Integer> counts = new HashMap<>();
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            Integer old = counts.get(c);
            counts.put(c, old == null ? 1 : old + 1);
        }
        int max = 0;
        for (Map.Entry<Character, Integer> e : counts.entrySet()) {
            if (e.getValue() > max) {
                max = e.getValue();
            }
        }
        return max;
    }

    public static long mixedWidths(int a, long b, double d) {
        long total = a + b;
        total += (long) (d * 2);
        float f = a / 3f;
        return total + (long) f + ((long) a * a);
    }

    public static int whileWithAndOr(int[] v, int limit) {
        int i = 0;
        while (i < v.length && (v[i] < limit || v[i] % 2 == 0)) {
            i++;
        }
        return i;
    }

    public static int nestedFinally(int x) {
        int r = 1;
        try {
            try {
                r *= 10 / x;
            } finally {
                r += 5;
            }
        } catch (ArithmeticException e) {
            r = -r;
        }
        return r;
    }

    public static List<Integer> evens(int n) {
        List<Integer> out = new ArrayList<>();
        for (int i = 0; i < n; i++) {
            if (i % 2 != 0) {
                continue;
            }
            out.add(i);
        }
        return out;
    }

    public static void main(String[] args) {
        int[][] grids = { {3, 4, -1, 5}, {7, 8, 9}, {1, 2, 3} };
        StringBuilder out = new StringBuilder();
        out.append(sumUntil(new int[] {1, 2, -3, 4, 5}, 5)).append(' ').append(sumUntil(new int[] {1, 7}, 5)).append('\n');
        out.append(findPair(grids, 8)).append(' ').append(findPair(grids, 5)).append(' ').append(findPair(grids, 42)).append('\n');
        out.append(digits(0)).append(' ').append(digits(12345)).append(' ').append(digits(-99)).append('\n');
        out.append(collatz(27)).append(' ').append(collatz(1)).append('\n');
        for (int a = -1; a <= 30; a += 13) {
            out.append(classify(a, 5, true)).append('|').append(classify(a, 5, false)).append('|');
        }
        out.append('\n');
        out.append(inRange(3, 1, 5)).append(inRange(0, 1, 5)).append(anyNegative(new int[] {1, -2})).append(anyNegative(new int[] {1})).append('\n');
        out.append(countVowels("Decompilers Are Fun")).append(grade(95)).append(grade(85)).append(grade(72)).append(grade(10)).append('\n');
        out.append(shout("hello, World")).append(checksum(new byte[] {1, 2, 3, -128, 127})).append('\n');
        for (int k = 0; k <= 6; k++) {
            out.append(fallThrough(k)).append(',');
        }
        out.append(switchInLoop(new int[] {1, 0, 2, 7})).append(' ').append(switchInLoop(new int[] {1, 9})).append('\n');
        out.append(parseOr(" 12 ", 3)).append(parseOr("x", 3)).append(parseOr(null, 3)).append('\n');
        out.append(withFinally(5)).append(withFinally(-5)).append(withFinally(0)).append(counter).append('\n');
        out.append(retryLoop(new String[] {"1", null, "x", "5", "", "100"})).append('\n');
        out.append(locked(3)).append(' ').append(locked(9)).append('\n');
        out.append(nestedTernary(1, 2, 3)).append(nestedTernary(3, 2, 1)).append(nestedTernary(2, 3, 1)).append('\n');
        out.append(xorLike(true, false)).append(xorLike(true, true)).append(xorLike(false, false)).append('\n');
        out.append(earlyExits(null)).append(' ').append(earlyExits(new int[] {-5, -3})).append(' ').append(earlyExits(new int[] {4, 0})).append('\n');
        List<String> words = new ArrayList<>();
        words.add("a");
        words.add("");
        words.add("bc");
        out.append(joinWords(words)).append(histogram("mississippi")).append('\n');
        out.append(mixedWidths(7, 1L << 40, 2.75)).append(' ').append(whileWithAndOr(new int[] {1, 2, 9, 10, 11}, 5)).append('\n');
        out.append(nestedFinally(2)).append(nestedFinally(0)).append(evens(7)).append('\n');
        System.out.print(out);
    }
}
