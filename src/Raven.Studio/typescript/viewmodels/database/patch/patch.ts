import app = require("durandal/app");
import viewModelBase = require("viewmodels/viewModelBase");
import patchDocument = require("models/database/patch/patchDocument");
import aceEditorBindingHandler = require("common/bindingHelpers/aceEditorBindingHandler");
import getDatabaseStatsCommand = require("commands/resources/getDatabaseStatsCommand");
import document = require("models/database/documents/document");
import database = require("models/resources/database");
import messagePublisher = require("common/messagePublisher");
import getDocumentWithMetadataCommand = require("commands/database/documents/getDocumentWithMetadataCommand");
import patchCommand = require("commands/database/patch/patchCommand");
import eventsCollector = require("common/eventsCollector");
import notificationCenter = require("common/notifications/notificationCenter");
import documentBasedColumnsProvider = require("widgets/virtualGrid/columns/providers/documentBasedColumnsProvider");
import popoverUtils = require("common/popoverUtils");
import deleteDocumentsCommand = require("commands/database/documents/deleteDocumentsCommand");
import documentPropertyProvider = require("common/helpers/database/documentPropertyProvider");
import getDocumentsPreviewCommand = require("commands/database/documents/getDocumentsPreviewCommand");
import defaultAceCompleter = require("common/defaultAceCompleter");
import queryCompleter = require("common/queryCompleter");
import patchSyntax = require("viewmodels/database/patch/patchSyntax");
import patchTester = require("viewmodels/database/patch/patchTester");
import savedPatchesStorage = require("common/storage/savedPatchesStorage");
import queryUtil = require("common/queryUtil");

type fetcherType = (skip: number, take: number, previewCols: string[], fullCols: string[]) => JQueryPromise<pagedResult<document>>;

class patchList {

    previewItem = ko.observable<patchDto>();

    allPatches = ko.observableArray<patchDto>([]);  

    private readonly useHandler: (patch: patchDto) => void;
    private readonly removeHandler: (patch: patchDto) => void;

    hasAnySavedPatch = ko.pureComputed(() => this.allPatches().length > 0);

    previewCode = ko.pureComputed(() => {
        const item = this.previewItem();
        if (!item) {
            return "";
        }

        return item.Query;
    });

    constructor(useHandler: (patch: patchDto) => void, removeHandler: (patch: patchDto) => void) {
        _.bindAll(this, ...["previewPatch", "removePatch", "usePatch"] as Array<keyof this>);
        this.useHandler = useHandler;
        this.removeHandler = removeHandler;
    }

    filteredPatches = ko.pureComputed(() => {
        let text = this.filters.searchText();

        if (!text) {
            return this.allPatches();
        }

        text = text.toLowerCase();

        return this.allPatches().filter(x => x.Name.toLowerCase().includes(text));
    });

    filters = {
        searchText: ko.observable<string>()
    };

    previewPatch(item: patchDto) {
        this.previewItem(item);
    }

    usePatch() {
        this.useHandler(this.previewItem());
    }

    removePatch(item: patchDto) {
        if (this.previewItem() === item) {
            this.previewItem(null);
        }
        this.removeHandler(item);
    }

    loadAll(db: database) {

        savedPatchesStorage.getSavedPatchesWithIndexNameCheck(db)
            .done(queries => this.allPatches(queries));
    }     

    push(doc: patchDto) {
        if (this.allPatches().find(x => x.Name === doc.Name)) {
            messagePublisher.reportError("Name already exists");
            return;
        }

        this.allPatches.push(doc);
    }
}

class patch extends viewModelBase {

    static readonly $body = $("body");
    static readonly ContainerSelector = "#patchContainer";

    static lastQuery = new Map<string, string>();

    inSaveMode = ko.observable<boolean>();

    spinners = {
        save: ko.observable<boolean>(false),
    };

    jsCompleter = defaultAceCompleter.completer();
    private indexes = ko.observableArray<Raven.Client.Documents.Operations.IndexInformation>();
    queryCompleter: queryCompleter;
    
    private documentsProvider: documentBasedColumnsProvider;
    private fullDocumentsProvider: documentPropertyProvider;

    patchDocument = ko.observable<patchDocument>(new patchDocument());

    runPatchValidationGroup: KnockoutValidationGroup;
    savePatchValidationGroup: KnockoutValidationGroup;

    savedPatches = new patchList(item => this.usePatch(item), item => this.removePatch(item));

    test = new patchTester(this.patchDocument().query, this.activeDatabase);

    private hideSavePatchHandler = (e: Event) => {
        if ($(e.target).closest(".patch-save").length === 0) {
            this.inSaveMode(false);
        }
    };

    constructor() {
        super();
        aceEditorBindingHandler.install();

        this.queryCompleter = queryCompleter.remoteCompleter(this.activeDatabase, this.indexes, "Update");

        this.initValidation();

        this.initObservables();
    }

    private initValidation() {
        const doc = this.patchDocument();

        doc.query.extend({
            required: true,
            aceValidation: true
        });
        
        this.patchDocument().name.extend({
            required: true
        });

        this.runPatchValidationGroup = ko.validatedObservable({
            query: doc.query,
        });

        this.savePatchValidationGroup = ko.validatedObservable({
            patchSaveName: this.patchDocument().name
        });
    }

    private initObservables() {
        this.inSaveMode.subscribe(enabled => {
            const $input = $(".patch-save .form-control");
            if (enabled) {
                $input.show();
                window.addEventListener("click", this.hideSavePatchHandler, true);
            } else {
                this.savePatchValidationGroup.errors.showAllMessages(false);
                window.removeEventListener("click", this.hideSavePatchHandler, true);
                setTimeout(() => $input.hide(), 200);
            }
        });
    }

    activate(recentPatchHash?: string) {
        super.activate(recentPatchHash);
        this.updateHelpLink("QGGJR5");

        this.fullDocumentsProvider = new documentPropertyProvider(this.activeDatabase());

        this.loadLastQuery();

        return $.when<any>(this.fetchAllIndexes(this.activeDatabase()), this.savedPatches.loadAll(this.activeDatabase()));
    }

    private loadLastQuery() {

        const myLastQuery = patch.lastQuery.get(this.activeDatabase().name);

        if (myLastQuery)
            this.patchDocument().query(myLastQuery);
    }


    deactivate(): void {
        super.deactivate();

        const queryText = this.patchDocument().query();
        this.saveLastQuery(queryText);
    }

    private saveLastQuery(queryText: string) {
        patch.lastQuery.set(this.activeDatabase().name, queryText);
    }

    attached() {
        super.attached();

        this.createKeyboardShortcut("ctrl+enter", () => {
            if (this.test.testMode()) {
                this.test.runTest();
            } else {
                this.runPatch();
            }
        }, patch.ContainerSelector);
        
        const jsCode = Prism.highlight("this.NewProperty = this.OldProperty + myParameter;\r\n" +
            "delete this.UnwantedProperty;\r\n" +
            "this.Comments.RemoveWhere(function(comment){\r\n" +
            "  return comment.Spam;\r\n" +
            "});",
            (Prism.languages as any).javascript);

        popoverUtils.longWithHover($(".patch-title small"),
            {
                content: `<p>Patch Scripts are written in JavaScript. <br />Examples: <pre>${jsCode}</pre></p>`
                + `<p>You can use following functions in your patch script:</p>`
                + `<ul>`
                + `<li><code>PutDocument(documentId, document)</code> - puts document with given name and data</li>`
                + `<li><code>LoadDocument(documentIdToLoad)</code> - loads document by id`
                + `<li><code>output(message)</code> - allows to output debug info when testing patches</li>`
                + `</ul>`
            });
    }

    private showPreview(doc: document) {
        // if document doesn't have all properties fetch them and then display preview

        const meta = doc.__metadata as any;
        const hasCollapsedFields = meta[getDocumentsPreviewCommand.ObjectStubsKey] || meta[getDocumentsPreviewCommand.ArrayStubsKey] || meta[getDocumentsPreviewCommand.TrimmedValueKey];

        if (hasCollapsedFields) {
            new getDocumentWithMetadataCommand(doc.getId(), this.activeDatabase(), true)
                .execute()
                .done((fullDocument: document) => {
                    documentBasedColumnsProvider.showPreview(fullDocument);
                });
        } else {
            // document has all properties - fallback to default method
            documentBasedColumnsProvider.showPreview(doc);
        }
    }

    usePatch(item: patchDto) {
        const patchDoc = this.patchDocument();
        patchDoc.copyFrom(item);
    }

    removePatch(item: patchDto) {

        this.confirmationMessage("Patch", `Are you sure you want to delete patch '${item.Name}'?`, ["Cancel", "Delete"])
            .done(result => {
                if (result.can) {
                    savedPatchesStorage.removeSavedPatchByName(this.activeDatabase(), item.Name);
                    this.savedPatches.loadAll(this.activeDatabase());
                }
            });
    }

    runPatch() {
        if (this.isValid(this.runPatchValidationGroup)) {
            this.patchOnQuery();
        }
    }

    savePatch() {

        if (this.inSaveMode()) {
            eventsCollector.default.reportEvent("patch", "save");

            if (this.isValid(this.savePatchValidationGroup)) {
                this.spinners.save(true);           

                this.savePatchInStorage()
                    .always(() => this.spinners.save(false))
                    .done(() => {
                        this.inSaveMode(false);
                        this.patchDocument().name("");
                        this.savePatchValidationGroup.errors.showAllMessages(false);
                        this.savedPatches.loadAll(this.activeDatabase());

                        messagePublisher.reportSuccess("Patch saved succesfully");
                    });;;
            }
        } else {
            if (this.isValid(this.runPatchValidationGroup)) {
                this.inSaveMode(true);    
            }
        }
    }

    private saveRecentPatch() {
        const name = this.getRecentPatchName();
        this.patchDocument().name(name);
        this.savePatchInStorage();
        this.patchDocument().name("");
    }

    private savePatchInStorage() {
        this.savedPatches.push(this.patchDocument().toDto());
        return $.when(savedPatchesStorage.storeSavedPatches(this.activeDatabase(), this.savedPatches.allPatches()));
    }

    private getRecentPatchName(): string {

        const collectionIndexName = queryUtil.getCollectionOrIndexName(this.patchDocument().query());

        return "Recent Patch (" + collectionIndexName + ")";
    }

    private patchOnQuery() {
        eventsCollector.default.reportEvent("patch", "run");
        const message = `Are you sure you want to apply this patch to matching documents?`;

        this.confirmationMessage("Patch", message, ["Cancel", "Patch all"])
            .done(result => {
                if (result.can) {
                    new patchCommand(this.patchDocument().query(), this.activeDatabase())
                        .execute()
                        .done((operation: operationIdDto) => {
                            notificationCenter.instance.openDetailsForOperationById(this.activeDatabase(), operation.OperationId);
                            this.saveLastQuery("");
                            this.saveRecentPatch();
                        });
                }
            });
    }

    private fetchAllIndexes(db: database): JQueryPromise<any> {
        return new getDatabaseStatsCommand(db)
            .execute()
            .done((results: Raven.Client.Documents.Operations.DatabaseStatistics) => {
                this.indexes(results.Indexes);
            });
    }

    enterTestMode() {
        this.test.enterTestMode('');
    }

    syntaxHelp() {
        const viewModel = new patchSyntax();
        app.showBootstrapDialog(viewModel);
    }
}

export = patch;
