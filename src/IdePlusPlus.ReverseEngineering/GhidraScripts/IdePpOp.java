// IDE++ Ghidra post-script. Run by analyzeHeadless as:
//   -postScript IdePpOp.java <op> <inputPath> <outPath>
// It performs one operation against the currently open (already-imported, already-analyzed) program
// and writes a small JSON result to <outPath>. Parameters are read from <inputPath>, a file of
// "key=value" lines whose values are Base64-encoded UTF-8 (except "limit"), so arbitrary text
// (comments, mangled names) never has to survive command-line quoting.
//
// Because read operations pass -readOnly and mutating ones (rename/comment) do not, rename and
// comment are committed and persist for later runs - the thing REA cannot do on Windows.
//
// Ops: overview | functions | decompile | xrefs | callgraph | rename | comment
//@category IDE++

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStreamWriter;
import java.io.Writer;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.Base64;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.CodeUnit;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceManager;
import ghidra.program.model.symbol.SourceType;

public class IdePpOp extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 3) {
            return;
        }

        String op = args[0];
        Map<String, String> in = readInput(args[1]);
        String outPath = args[2];
        StringBuilder json = new StringBuilder();

        try {
            switch (op) {
                case "overview":
                    overview(json);
                    break;
                case "functions":
                    functions(json, in.getOrDefault("filter", ""), parseInt(in.getOrDefault("limit", "200"), 200));
                    break;
                case "decompile":
                    decompile(json, in.getOrDefault("target", ""));
                    break;
                case "xrefs":
                    xrefs(json, in.getOrDefault("target", ""));
                    break;
                case "callgraph":
                    callgraph(json, in.getOrDefault("target", ""));
                    break;
                case "rename":
                    rename(json, in.getOrDefault("target", ""), in.getOrDefault("newName", ""));
                    break;
                case "comment":
                    comment(json, in.getOrDefault("target", ""), in.getOrDefault("comment", ""));
                    break;
                default:
                    error(json, "unknown op: " + op);
            }
        } catch (Exception e) {
            error(json, e.getClass().getSimpleName() + ": " + e.getMessage());
        }

        write(outPath, json.toString());
    }

    private void overview(StringBuilder j) {
        FunctionManager fm = currentProgram.getFunctionManager();
        j.append("{\"ok\":true,")
                .append("\"name\":\"").append(esc(currentProgram.getName())).append("\",")
                .append("\"languageId\":\"").append(esc(currentProgram.getLanguageID().getIdAsString())).append("\",")
                .append("\"compiler\":\"").append(esc(currentProgram.getCompilerSpec().getCompilerSpecID().getIdAsString())).append("\",")
                .append("\"imageBase\":\"").append(currentProgram.getImageBase().toString()).append("\",")
                .append("\"functions\":").append(fm.getFunctionCount()).append(",")
                .append("\"symbols\":").append(currentProgram.getSymbolTable().getNumSymbols())
                .append("}");
    }

    private void functions(StringBuilder j, String filter, int limit) {
        FunctionManager fm = currentProgram.getFunctionManager();
        j.append("{\"ok\":true,\"total\":").append(fm.getFunctionCount()).append(",\"functions\":[");
        boolean first = true;
        int shown = 0;
        String needle = filter == null ? "" : filter.toLowerCase();
        for (Function f : fm.getFunctions(true)) {
            if (!needle.isEmpty() && !f.getName().toLowerCase().contains(needle)) {
                continue;
            }
            if (shown >= limit) {
                break;
            }
            if (!first) {
                j.append(",");
            }
            first = false;
            j.append("{\"name\":\"").append(esc(f.getName())).append("\",\"address\":\"")
                    .append(f.getEntryPoint().toString()).append("\"}");
            shown++;
        }
        j.append("],\"shown\":").append(shown).append("}");
    }

    private void decompile(StringBuilder j, String target) {
        Function f = resolveFunction(target);
        if (f == null) {
            error(j, "function not found: " + target);
            return;
        }
        DecompInterface di = new DecompInterface();
        try {
            di.openProgram(currentProgram);
            DecompileResults res = di.decompileFunction(f, 60, monitor);
            if (res == null || !res.decompileCompleted() || res.getDecompiledFunction() == null) {
                error(j, "decompile failed: " + (res == null ? "null" : res.getErrorMessage()));
                return;
            }
            j.append("{\"ok\":true,\"name\":\"").append(esc(f.getName())).append("\",\"address\":\"")
                    .append(f.getEntryPoint().toString()).append("\",\"code\":\"")
                    .append(esc(res.getDecompiledFunction().getC())).append("\"}");
        } finally {
            di.dispose();
        }
    }

    private void xrefs(StringBuilder j, String target) {
        Function f = resolveFunction(target);
        if (f == null) {
            error(j, "function not found: " + target);
            return;
        }
        ReferenceManager rm = currentProgram.getReferenceManager();
        j.append("{\"ok\":true,\"name\":\"").append(esc(f.getName())).append("\",\"to\":[");
        boolean first = true;
        int count = 0;
        for (Reference r : rm.getReferencesTo(f.getEntryPoint())) {
            if (count >= 200) {
                break;
            }
            Address from = r.getFromAddress();
            Function containing = currentProgram.getFunctionManager().getFunctionContaining(from);
            if (!first) {
                j.append(",");
            }
            first = false;
            j.append("{\"from\":\"").append(from.toString()).append("\",\"fromFunction\":\"")
                    .append(esc(containing == null ? "" : containing.getName())).append("\"}");
            count++;
        }
        j.append("]}");
    }

    private void callgraph(StringBuilder j, String target) {
        Function f = resolveFunction(target);
        if (f == null) {
            error(j, "function not found: " + target);
            return;
        }
        Set<Function> callers = f.getCallingFunctions(monitor);
        Set<Function> callees = f.getCalledFunctions(monitor);
        j.append("{\"ok\":true,\"name\":\"").append(esc(f.getName())).append("\",\"callers\":[");
        appendNames(j, callers);
        j.append("],\"callees\":[");
        appendNames(j, callees);
        j.append("]}");
    }

    private void rename(StringBuilder j, String target, String newName) throws Exception {
        Function f = resolveFunction(target);
        if (f == null) {
            error(j, "function not found: " + target);
            return;
        }
        String oldName = f.getName();
        int tx = currentProgram.startTransaction("IDE++ rename");
        boolean ok = false;
        try {
            f.setName(newName, SourceType.USER_DEFINED);
            ok = true;
        } finally {
            currentProgram.endTransaction(tx, ok);
        }
        j.append("{\"ok\":true,\"oldName\":\"").append(esc(oldName)).append("\",\"newName\":\"")
                .append(esc(newName)).append("\",\"address\":\"").append(f.getEntryPoint().toString()).append("\"}");
    }

    private void comment(StringBuilder j, String target, String text) {
        Function f = resolveFunction(target);
        if (f == null) {
            error(j, "function not found: " + target);
            return;
        }
        int tx = currentProgram.startTransaction("IDE++ comment");
        boolean ok = false;
        try {
            currentProgram.getListing().setComment(f.getEntryPoint(), CodeUnit.PLATE_COMMENT, text);
            ok = true;
        } finally {
            currentProgram.endTransaction(tx, ok);
        }
        j.append("{\"ok\":true,\"address\":\"").append(f.getEntryPoint().toString()).append("\",\"comment\":\"")
                .append(esc(text)).append("\"}");
    }

    private Function resolveFunction(String target) {
        if (target == null || target.isEmpty()) {
            return null;
        }
        FunctionManager fm = currentProgram.getFunctionManager();
        for (Function f : fm.getFunctions(true)) {
            if (f.getName().equals(target)) {
                return f;
            }
        }
        try {
            Address addr = currentProgram.getAddressFactory().getAddress(target);
            if (addr != null) {
                Function f = fm.getFunctionContaining(addr);
                if (f != null) {
                    return f;
                }
            }
        } catch (Exception ignored) {
            // Not an address; fall through.
        }
        return null;
    }

    private void appendNames(StringBuilder j, Set<Function> functions) {
        boolean first = true;
        for (Function f : functions) {
            if (!first) {
                j.append(",");
            }
            first = false;
            j.append("\"").append(esc(f.getName())).append("\"");
        }
    }

    private Map<String, String> readInput(String path) throws IOException {
        Map<String, String> map = new HashMap<>();
        if (path == null || path.isEmpty() || !new File(path).exists()) {
            return map;
        }
        List<String> lines = Files.readAllLines(new File(path).toPath(), StandardCharsets.UTF_8);
        for (String line : lines) {
            int eq = line.indexOf('=');
            if (eq <= 0) {
                continue;
            }
            String key = line.substring(0, eq);
            String value = line.substring(eq + 1);
            if (key.equals("limit")) {
                map.put(key, value);
            } else {
                map.put(key, new String(Base64.getDecoder().decode(value), StandardCharsets.UTF_8));
            }
        }
        return map;
    }

    private void error(StringBuilder j, String message) {
        j.setLength(0);
        j.append("{\"ok\":false,\"error\":\"").append(esc(message)).append("\"}");
    }

    private void write(String path, String content) throws IOException {
        try (Writer w = new OutputStreamWriter(new FileOutputStream(path), StandardCharsets.UTF_8)) {
            w.write(content);
        }
    }

    private static int parseInt(String value, int fallback) {
        try {
            return Integer.parseInt(value.trim());
        } catch (NumberFormatException e) {
            return fallback;
        }
    }

    private static String esc(String s) {
        if (s == null) {
            return "";
        }
        StringBuilder b = new StringBuilder(s.length() + 16);
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"':
                    b.append("\\\"");
                    break;
                case '\\':
                    b.append("\\\\");
                    break;
                case '\n':
                    b.append("\\n");
                    break;
                case '\r':
                    b.append("\\r");
                    break;
                case '\t':
                    b.append("\\t");
                    break;
                default:
                    if (c < 0x20) {
                        b.append(String.format("\\u%04x", (int) c));
                    } else {
                        b.append(c);
                    }
            }
        }
        return b.toString();
    }
}
