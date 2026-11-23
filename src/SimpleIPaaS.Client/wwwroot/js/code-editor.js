// Monaco-based code editor interop for SimpleIPaaS.
// The editor is vendored locally under wwwroot/vendor/monaco (AMD build) so the
// app keeps working fully offline.

const VS_BASE = new URL('vendor/monaco/vs', document.baseURI).href;
const LOADER_URL = VS_BASE + '/loader.js';

let monacoPromise = null;
let loaderRequested = false;
const editors = new Map();
let nextEditorId = 1;

// Used by the mapper modal after a maximize/restore toggle. Registered here
// (instead of index.html) so the module URL never has to be fingerprinted.
window.simpleIPaaSCodeEditor = window.simpleIPaaSCodeEditor || {
    layoutAll: function () {
        editors.forEach(state => {
            try { state.editor.layout(); } catch { }
        });
    }
};

const IPAAS_LIGHT = {
    base: 'vs',
    inherit: true,
    rules: [
        { token: 'comment', foreground: '6c7086', fontStyle: 'italic' },
        { token: 'keyword', foreground: 'cba6f7' },
        { token: 'string', foreground: 'a6e3a1' },
        { token: 'number', foreground: 'fab387' },
        { token: 'type', foreground: 'f9e2af' },
        { token: 'type.identifier', foreground: 'f9e2af' },
        { token: 'identifier', foreground: 'cdd6f4' },
        { token: 'delimiter', foreground: '9399b2' },
        { token: 'operator', foreground: '89dceb' },
        { token: 'namespace', foreground: 'f9e2af' }
    ],
    colors: {
        'editor.background': '#161a27',
        'editor.foreground': '#cdd6f4',
        'editor.lineHighlightBackground': '#1d2233',
        'editorLineNumber.foreground': '#4d5470',
        'editorLineNumber.activeForeground': '#9399b2',
        'editor.selectionBackground': '#33405e',
        'editor.inactiveSelectionBackground': '#2a3350',
        'editorCursor.foreground': '#8b95ff',
        'editorIndentGuide.background1': '#232a3d',
        'editorIndentGuide.activeBackground1': '#3b4360',
        'editorWidget.background': '#1b2030',
        'editorWidget.border': '#2c3348',
        'editorSuggestWidget.background': '#1b2030',
        'editorSuggestWidget.border': '#2c3348',
        'editorSuggestWidget.selectedBackground': '#2a3350',
        'editorSuggestWidget.highlightForeground': '#8b95ff',
        'editorHoverWidget.background': '#1b2030',
        'editorHoverWidget.border': '#2c3348',
        'editorBracketHighlight.foreground1': '#89b4fa',
        'editorBracketHighlight.foreground2': '#cba6f7',
        'editorBracketHighlight.foreground3': '#a6e3a1',
        'scrollbarSlider.background': '#2c334880',
        'scrollbarSlider.hoverBackground': '#3b4360a0',
        'scrollbarSlider.activeBackground': '#4d5470c0',
        'editorGutter.background': '#161a27'
    }
};

const IPAAS_DARK = {
    base: 'vs-dark',
    inherit: true,
    rules: IPAAS_LIGHT.rules,
    colors: Object.assign({}, IPAAS_LIGHT.colors, {
        'editor.background': '#0f121b',
        'editor.lineHighlightBackground': '#171c29',
        'editorGutter.background': '#0f121b',
        'editorIndentGuide.background1': '#1b2130',
        'editorWidget.background': '#141926',
        'editorSuggestWidget.background': '#141926',
        'editorHoverWidget.background': '#141926'
    })
};

const KEYWORDS = [
    'using', 'var', 'return', 'if', 'else', 'else if', 'foreach', 'for', 'while', 'do', 'switch', 'case',
    'default', 'break', 'continue', 'new', 'true', 'false', 'null', 'this', 'base', 'try', 'catch',
    'finally', 'throw', 'async', 'await', 'yield', 'is', 'as', 'in', 'out', 'ref', 'when',
    'public', 'private', 'protected', 'internal', 'static', 'readonly', 'const', 'sealed', 'override',
    'virtual', 'abstract', 'class', 'interface', 'struct', 'enum', 'namespace', 'void',
    'string', 'int', 'long', 'double', 'decimal', 'bool', 'char', 'object', 'dynamic',
    'DateTime', 'TimeSpan', 'Guid', 'Math', 'Task', 'StringComparison', 'StringSplitOptions'
];

const SNIPPETS = [
    {
        label: 'JObject.Parse',
        insertText: 'JObject.Parse(${1:FlowStateJson})',
        detail: 'Parse a JSON string into a mutable Newtonsoft object.',
        documentation: 'Requires `using Newtonsoft.Json.Linq;`.\n\n`var state = JObject.Parse(FlowStateJson);`'
    },
    {
        label: 'JArray.Parse',
        insertText: 'JArray.Parse(${1:json})',
        detail: 'Parse a JSON array string into a Newtonsoft array.',
        documentation: 'Requires `using Newtonsoft.Json.Linq;`.'
    },
    {
        label: 'JsonConvert.SerializeObject',
        insertText: 'JsonConvert.SerializeObject(${1:value})',
        detail: 'Serialize a value to a JSON string.',
        documentation: 'Requires `using Newtonsoft.Json;`.'
    },
    {
        label: 'JsonConvert.DeserializeObject',
        insertText: 'JsonConvert.DeserializeObject<${1:JObject}>(${2:json})',
        detail: 'Deserialize a JSON string into a typed value.',
        documentation: 'Requires `using Newtonsoft.Json;`.'
    },
    {
        label: 'state token lookup',
        insertText: '[${1:"NodeName"}]',
        detail: 'Index a JObject/JToken by property name.',
        documentation: 'Chain lookups like `state["GetUsers"]["id"]?.Value<string>()`.'
    },
    {
        label: 'foreach item in array',
        insertText: 'foreach (var ${1:item} in ${2:array})\n{\n\t$0\n}',
        detail: 'Iterate a JArray or any IEnumerable.',
        documentation: ''
    },
    {
        label: 'if block',
        insertText: 'if (${1:condition})\n{\n\t$0\n}',
        detail: 'Conditional block.',
        documentation: ''
    },
    {
        label: 'DateTime.UtcNow.ToString("O")',
        insertText: 'DateTime.UtcNow.ToString("O")',
        detail: 'Round-trippable UTC timestamp.',
        documentation: ''
    }
];

const GLOBAL_VARIABLES = [
    { label: 'FlowStateJson', detail: 'string — shared flow state as JSON (input to this card).' },
    { label: 'PersistedStateJson', detail: 'string — durable persisted state as JSON.' },
    { label: 'HttpResponseJson', detail: 'string — HTTP response body (post-flight scripts only).' }
];

function registerThemes(monaco) {
    monaco.editor.defineTheme('ipaas-code-light', IPAAS_LIGHT);
    monaco.editor.defineTheme('ipaas-code-dark', IPAAS_DARK);
}

function themeNameForDocument() {
    const mode = document.documentElement.getAttribute('data-theme');
    return mode === 'dark' ? 'ipaas-code-dark' : 'ipaas-code-light';
}

function registerCSharpCompletions(monaco) {
    const keywordItems = KEYWORDS.map(keyword => ({
        label: keyword,
        kind: monaco.languages.CompletionItemKind.Keyword,
        insertText: keyword
    }));

    const snippetItems = SNIPPETS.map(snippet => ({
        label: snippet.label,
        kind: monaco.languages.CompletionItemKind.Snippet,
        insertText: snippet.insertText,
        insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
        detail: snippet.detail,
        documentation: snippet.documentation
    }));

    const variableItems = GLOBAL_VARIABLES.map(variable => ({
        label: variable.label,
        kind: monaco.languages.CompletionItemKind.Variable,
        insertText: variable.label,
        detail: variable.detail,
        documentation: variable.detail
    }));

    monaco.languages.registerCompletionItemProvider('csharp', {
        triggerCharacters: ['.', '"'],
        provideCompletionItems: (model, position) => {
            const word = model.getWordUntilPosition(position);
            const range = {
                startLineNumber: position.lineNumber,
                endLineNumber: position.lineNumber,
                startColumn: word.startColumn,
                endColumn: word.endColumn
            };
            return {
                suggestions: keywordItems.concat(snippetItems, variableItems).map(item => Object.assign({}, item, { range }))
            };
        }
    });

    monaco.languages.setLanguageConfiguration('csharp', {
        comments: { lineComment: '//', blockComment: ['/*', '*/'] },
        brackets: [['{', '}'], ['[', ']'], ['(', ')']],
        autoClosingPairs: [
            { open: '{', close: '}' },
            { open: '[', close: ']' },
            { open: '(', close: ')' },
            { open: '"', close: '"', notIn: ['string'] },
            { open: "'", close: "'", notIn: ['string', 'comment'] },
            { open: '/*', close: '*/', notIn: ['string'] }
        ],
        surroundingPairs: [
            { open: '{', close: '}' },
            { open: '[', close: ']' },
            { open: '(', close: ')' },
            { open: '"', close: '"' },
            { open: "'", close: "'" }
        ]
    });
}

function watchThemeChanges(monaco) {
    if (document.documentElement.dataset.ipaasMonacoThemeWatch === '1') return;
    document.documentElement.dataset.ipaasMonacoThemeWatch = '1';
    new MutationObserver(() => {
        monaco.editor.setTheme(themeNameForDocument());
    }).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
}

function workerUrlFor(label) {
    const base = new URL('vendor/monaco/', document.baseURI).href;
    const entry = label === 'json'
        ? 'vs/language/json/json.worker'
        : label === 'css' || label === 'scss' || label === 'less'
            ? 'vs/language/css/css.worker'
            : label === 'html' || label === 'handlebars' || label === 'razor'
                ? 'vs/language/html/html.worker'
                : 'vs/editor/editor.worker';
    const bootstrap = [
        `self.MonacoEnvironment = { baseUrl: ${JSON.stringify(base)} };`,
        `importScripts(${JSON.stringify(base + 'vs/loader.js')});`,
        `require.config({ paths: { vs: ${JSON.stringify(base + 'vs')} } });`,
        `require([${JSON.stringify(entry)}], function () { self.postMessage('ready'); }, function (err) { console.error('monaco worker failed', err); });`
    ].join('\n');
    return URL.createObjectURL(new Blob([bootstrap], { type: 'text/javascript' }));
}

function installEnvironment() {
    if (window.MonacoEnvironment) return;
    window.MonacoEnvironment = {
        getWorkerUrl: (moduleId, label) => workerUrlFor(label),
        getWorker: (moduleId, label) => new Worker(workerUrlFor(label))
    };
}

function ensureMonaco() {
    if (monacoPromise) return monacoPromise;
    installEnvironment();
    monacoPromise = new Promise((resolve, reject) => {
        const boot = () => {
            try {
                window.require.config({ paths: { vs: VS_BASE } });
                window.require(['vs/editor/editor.main'], () => {
                    const monaco = window.monaco;
                    registerThemes(monaco);
                    registerCSharpCompletions(monaco);
                    watchThemeChanges(monaco);
                    resolve(monaco);
                }, reject);
            } catch (error) {
                reject(error);
            }
        };

        if (window.require && window.require.config) {
            boot();
            return;
        }
        if (loaderRequested) {
            reject(new Error('Monaco loader is still loading.'));
            return;
        }
        loaderRequested = true;
        const script = document.createElement('script');
        script.src = LOADER_URL;
        script.onload = boot;
        script.onerror = () => reject(new Error('Unable to load the Monaco loader from ' + LOADER_URL));
        document.head.appendChild(script);
    });
    return monacoPromise;
}

// Warms up Monaco (loader + editor bundle + completions) ahead of the first
// modal open so the editor appears with its code immediately.
export function preload() {
    return ensureMonaco();
}

export async function create(hostId, initialValue, language, readOnly, dotNetRef) {
    const monaco = await ensureMonaco();
    const host = document.getElementById(hostId);
    if (!host) throw new Error('Code editor host ' + hostId + ' was not found.');

    const editor = monaco.editor.create(host, {
        value: initialValue || '',
        language: language || 'csharp',
        theme: themeNameForDocument(),
        readOnly: !!readOnly,
        automaticLayout: true,
        fontSize: 13,
        fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', Consolas, 'Courier New', monospace",
        lineHeight: 21,
        tabSize: 4,
        insertSpaces: true,
        detectIndentation: false,
        autoIndent: 'full',
        formatOnType: true,
        formatOnPaste: true,
        autoClosingBrackets: 'always',
        autoClosingQuotes: 'always',
        autoSurround: 'languageDefined',
        bracketPairColorization: { enabled: true },
        guides: { indentation: true, bracketPairs: true },
        suggestOnTriggerCharacters: true,
        quickSuggestions: { other: true, comments: false, strings: true },
        wordBasedSuggestions: 'currentDocument',
        tabCompletion: 'on',
        acceptSuggestionOnEnter: 'on',
        parameterHints: { enabled: true },
        minimap: { enabled: false },
        scrollBeyondLastLine: false,
        // Instant caret reveal: smooth scrolling makes the editor lag behind
        // when typing quickly and the caret moves to an off-screen line.
        smoothScrolling: false,
        cursorSurroundingLines: 2,
        fixedOverflowWidgets: true,
        lineNumbersMinChars: 3,
        padding: { top: 8, bottom: 8 },
        renderLineHighlight: 'line',
        cursorBlinking: 'blink',
        scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10 },
        wordWrap: readOnly ? 'on' : 'off',
        overviewRulerBorder: false,
        hideCursorInOverviewRuler: true,
        renderValidationDecorations: 'off'
    });

    if (readOnly) {
        editor.updateOptions({ domReadOnly: true });
    }

    const id = nextEditorId++;
    const state = { editor, dotNetRef, suppress: false };
    editors.set(id, state);

    editor.onDidChangeModelContent(() => {
        if (state.suppress) return;
        const value = editor.getValue();
        state.dotNetRef.invokeMethodAsync('OnEditorChanged', value).catch(() => { });
    });

    return id;
}

export function setValue(id, value) {
    const state = editors.get(id);
    if (!state) return;
    const next = value == null ? '' : value;
    if (state.editor.getValue() === next) return;
    state.suppress = true;
    try {
        state.editor.setValue(next);
        state.editor.setScrollTop(0);
    } finally {
        state.suppress = false;
    }
}

export function setReadOnly(id, readOnly) {
    const state = editors.get(id);
    if (!state) return;
    state.editor.updateOptions({ readOnly: !!readOnly, domReadOnly: !!readOnly });
}

export function layout(id) {
    const state = editors.get(id);
    if (state) state.editor.layout();
}

export function focus(id) {
    const state = editors.get(id);
    if (state) state.editor.focus();
}

export function dispose(id) {
    const state = editors.get(id);
    if (!state) return;
    editors.delete(id);
    state.editor.getModel()?.dispose();
    state.editor.dispose();
}
