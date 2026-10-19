namespace XRay.OneNote.Internal.OneStore;

/// <summary>MS-ONE property ids (section 2.1.12) this reader names. Unnamed ids are keyed as "0x" + hex id.</summary>
internal static class PropertyNames
{
    public const string PageWidth = "PageWidth";
    public const string OutlineElementChildLevel = "OutlineElementChildLevel";
    public const string Bold = "Bold";
    public const string Italic = "Italic";
    public const string Underline = "Underline";
    public const string Strikethrough = "Strikethrough";
    public const string Superscript = "Superscript";
    public const string Subscript = "Subscript";
    public const string Font = "Font";
    public const string FontSize = "FontSize";
    public const string FontColor = "FontColor";
    public const string Highlight = "Highlight";
    public const string OffsetFromParentHoriz = "OffsetFromParentHoriz";
    public const string OffsetFromParentVert = "OffsetFromParentVert";
    public const string NumberListFormat = "NumberListFormat";
    public const string LayoutMaxWidth = "LayoutMaxWidth";
    public const string ContentChildNodes = "ContentChildNodes";
    public const string ElementChildNodes = "ElementChildNodes";
    public const string RichEditTextUnicode = "RichEditTextUnicode";
    public const string ListNodes = "ListNodes";
    public const string NotebookManagementEntityGuid = "NotebookManagementEntityGuid";
    public const string LanguageId = "LanguageID";
    public const string LayoutAlignmentInParent = "LayoutAlignmentInParent";
    public const string PictureContainer = "PictureContainer";
    public const string TopologyCreationTimeStamp = "TopologyCreationTimeStamp";
    public const string LayoutAlignmentSelf = "LayoutAlignmentSelf";
    public const string ListRestart = "ListRestart";
    public const string CachedTitleString = "CachedTitleString";
    public const string CreationTimeStamp = "CreationTimeStamp";
    public const string CachedTitleStringFromPage = "CachedTitleStringFromPage";
    public const string TableBordersVisible = "TableBordersVisible";
    public const string StructureElementChildNodes = "StructureElementChildNodes";
    public const string ChildGraphSpaceElementNodes = "ChildGraphSpaceElementNodes";
    public const string TableColumnWidths = "TableColumnWidths";
    public const string TableColumnsLocked = "TableColumnsLocked";
    public const string Author = "Author";
    public const string LastModifiedTimeStamp = "LastModifiedTimeStamp";
    public const string LastModifiedTime = "LastModifiedTime";
    public const string EmbeddedFileContainer = "EmbeddedFileContainer";
    public const string EmbeddedFileName = "EmbeddedFileName";
    public const string SourceFilepath = "SourceFilepath";
    public const string ImageFilename = "ImageFilename";
    public const string PageLevel = "PageLevel";
    public const string TextRunIndex = "TextRunIndex";
    public const string TextRunFormatting = "TextRunFormatting";
    public const string Hyperlink = "Hyperlink";
    public const string WzHyperlinkUrl = "WzHyperlinkUrl";
    public const string ImageAltText = "ImageAltText";
    public const string MetaDataObjectsAboveGraphSpace = "MetaDataObjectsAboveGraphSpace";
    public const string TextRunDataObject = "TextRunDataObject";
    public const string TextExtendedAscii = "TextExtendedAscii";
    public const string NoteTagShape = "NoteTagShape";
    public const string NoteTagHighlightColor = "NoteTagHighlightColor";
    public const string NoteTagTextColor = "NoteTagTextColor";
    public const string NoteTagLabel = "NoteTagLabel";
    public const string NoteTagCreated = "NoteTagCreated";
    public const string NoteTagCompleted = "NoteTagCompleted";
    public const string ActionItemStatus = "ActionItemStatus";
    public const string ParagraphAlignment = "ParagraphAlignment";
    public const string NoteTagDefinitionOid = "NoteTagDefinitionOid";
    public const string NoteTagStates = "NoteTagStates";
    public const string SectionDisplayName = "SectionDisplayName";
    public const string WebPictureContainer14 = "WebPictureContainer14";
    public const string PictureWidth = "PictureWidth";
    public const string PictureHeight = "PictureHeight";
    public const string LayoutMaxHeight = "LayoutMaxHeight";
    public const string IsBackground = "IsBackground";
    public const string SectionColor = "SectionColor";
    public const string CellBackgroundColor = "CellBackgroundColor";
    public const string ParagraphStyle = "ParagraphStyle";
    public const string ParagraphStyleId = "ParagraphStyleId";
    public const string PageRecognizedTextContainer = "PageRecognizedTextContainer";
    public const string RecognizedTextChildNodes = "RecognizedTextChildNodes";
    public const string RecognizedText = "RecognizedText";

    private static readonly Dictionary<uint, string> ById = new()
    {
        [0x1C01] = "PageWidth",
        [0x1C02] = "PageHeight",
        [0x1C03] = "OutlineElementChildLevel",
        [0x1C04] = "Bold",
        [0x1C05] = "Italic",
        [0x1C06] = "Underline",
        [0x1C07] = "Strikethrough",
        [0x1C08] = "Superscript",
        [0x1C09] = "Subscript",
        [0x1C0A] = "Font",
        [0x1C0B] = "FontSize",
        [0x1C0C] = "FontColor",
        [0x1C0D] = "Highlight",
        [0x1C14] = "OffsetFromParentHoriz",
        [0x1C15] = "OffsetFromParentVert",
        [0x1C1A] = "NumberListFormat",
        [0x1C1B] = "LayoutMaxWidth",
        [0x1C1C] = "LayoutMaxHeight",
        [0x1C1F] = "ContentChildNodes",
        [0x1C20] = "ElementChildNodes",
        [0x1C22] = "RichEditTextUnicode",
        [0x1C26] = "ListNodes",
        [0x1C30] = "NotebookManagementEntityGuid",
        [0x1C3B] = "LanguageID",
        [0x1C3E] = "LayoutAlignmentInParent",
        [0x1C3F] = "PictureContainer",
        [0x1C65] = "TopologyCreationTimeStamp",
        [0x1C84] = "LayoutAlignmentSelf",
        [0x1C87] = "IsTitleTime",
        [0x1C88] = "IsBoilerText",
        [0x1CB4] = "IsTitleText",
        [0x1CB5] = "IsTitleDate",
        [0x1CBE] = "SectionColor",
        [0x1D13] = "IsBackground",
        [0x1E26] = "CellBackgroundColor",
        [0x1CB7] = "ListRestart",
        [0x1CF3] = "CachedTitleString",
        [0x1CFE] = "RichEditTextLangID",
        [0x1D09] = "CreationTimeStamp",
        [0x1D3C] = "CachedTitleStringFromPage",
        [0x1D57] = "RowCount",
        [0x1D58] = "ColumnCount",
        [0x1D5E] = "TableBordersVisible",
        [0x1D5F] = "StructureElementChildNodes",
        [0x1D63] = "ChildGraphSpaceElementNodes",
        [0x1D66] = "TableColumnWidths",
        [0x1D75] = "Author",
        [0x1D77] = "LastModifiedTimeStamp",
        [0x1D78] = "AuthorOriginal",
        [0x1D79] = "AuthorMostRecent",
        [0x1D7A] = "LastModifiedTime",
        [0x1D7D] = "TableColumnsLocked",
        [0x1D9B] = "EmbeddedFileContainer",
        [0x1D9C] = "EmbeddedFileName",
        [0x1D9D] = "SourceFilepath",
        [0x1DD7] = "ImageFilename",
        [0x1DFF] = "PageLevel",
        [0x1E12] = "TextRunIndex",
        [0x1E13] = "TextRunFormatting",
        [0x1E14] = "Hyperlink",
        [0x1E20] = "WzHyperlinkUrl",
        [0x1E58] = "ImageAltText",
        [0x342C] = "ParagraphStyle",
        [0x3442] = "MetaDataObjectsAboveGraphSpace",
        [0x3458] = "TextRunDataObject",
        [0x3498] = "TextExtendedAscii",
        [0x3499] = "TextRunData",
        [0x345A] = "ParagraphStyleId",
        [0x3462] = "HasVersionPages",
        [0x3463] = "ActionItemType",
        [0x3464] = "NoteTagShape",
        [0x3465] = "NoteTagHighlightColor",
        [0x3466] = "NoteTagTextColor",
        [0x3467] = "NoteTagPropertyStatus",
        [0x3468] = "NoteTagLabel",
        [0x346E] = "NoteTagCreated",
        [0x346F] = "NoteTagCompleted",
        [0x3470] = "ActionItemStatus",
        [0x3473] = "ActionItemSchemaVersion",
        [0x3477] = "ParagraphAlignment",
        [0x347B] = "VersionHistoryGraphSpaceContextNodes",
        [0x3480] = "DisplayedPageNumber",
        [0x3488] = "NoteTagDefinitionOid",
        [0x3489] = "NoteTagStates",
        [0x349B] = "SectionDisplayName",
        [0x34C8] = "WebPictureContainer14",
        [0x34CD] = "PictureWidth",
        [0x35D7] = "PageRecognizedTextContainer",
        [0x35D9] = "RecognizedTextChildNodes",
        [0x35DA] = "RecognizedText",
        [0x34CE] = "PictureHeight",
    };

    private static readonly Dictionary<uint, string> UnnamedCache = new();

    public static string For(uint propertyId)
    {
        if (ById.TryGetValue(propertyId, out var name))
        {
            return name;
        }

        lock (UnnamedCache)
        {
            if (!UnnamedCache.TryGetValue(propertyId, out name))
            {
                name = "0x" + propertyId.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
                UnnamedCache[propertyId] = name;
            }
        }

        return name;
    }
}

/// <summary>MS-ONE JCID indices (section 2.1.13) this reader names. Unnamed indices are "0x" + hex index.</summary>
internal static class JcidNames
{
    public const string SectionNode = "SectionNode";
    public const string PageSeriesNode = "PageSeriesNode";
    public const string PageNode = "PageNode";
    public const string OutlineNode = "OutlineNode";
    public const string OutlineElementNode = "OutlineElementNode";
    public const string RichTextOENode = "RichTextOENode";
    public const string ImageNode = "ImageNode";
    public const string NumberListNode = "NumberListNode";
    public const string OutlineGroup = "OutlineGroup";
    public const string TableNode = "TableNode";
    public const string TableRowNode = "TableRowNode";
    public const string TableCellNode = "TableCellNode";
    public const string TitleNode = "TitleNode";
    public const string PageMetaData = "PageMetaData";
    public const string SectionMetaData = "SectionMetaData";
    public const string EmbeddedFileNode = "EmbeddedFileNode";
    public const string PageManifestNode = "PageManifestNode";
    public const string VersionHistoryContent = "VersionHistoryContent";
    public const string VersionProxy = "VersionProxy";
    public const string RevisionMetaData = "RevisionMetaData";
    public const string RecognizedTextRoot = "RecognizedTextRoot";
    public const string RecognizedTextWord = "RecognizedTextWord";

    private static readonly Dictionary<ushort, string> ByIndex = new()
    {
        [0x0001] = "ReadOnlyPersistablePropertyContainerForAuthor",
        [0x0007] = "SectionNode",
        [0x0008] = "PageSeriesNode",
        [0x000B] = "PageNode",
        [0x000C] = "OutlineNode",
        [0x000D] = "OutlineElementNode",
        [0x000E] = "RichTextOENode",
        [0x0011] = "ImageNode",
        [0x0012] = "NumberListNode",
        [0x0019] = "OutlineGroup",
        [0x0022] = "TableNode",
        [0x0023] = "TableRowNode",
        [0x0024] = "TableCellNode",
        [0x002C] = "TitleNode",
        [0x0030] = "PageMetaData",
        [0x0031] = "SectionMetaData",
        [0x0035] = "EmbeddedFileNode",
        [0x0037] = "PageManifestNode",
        [0x0038] = "ConflictPageMetaData",
        [0x003C] = "VersionHistoryContent",
        [0x003D] = "VersionProxy",
        [0x0043] = "NoteTagSharedDefinitionContainer",
        [0x0044] = "RevisionMetaData",
        [0x0046] = "VersionHistoryMetaData",
        [0x004D] = "ParagraphStyleObject",
        [0x0054] = "RecognizedTextRoot",
        [0x0055] = "RecognizedTextLine",
        [0x0056] = "RecognizedTextBlock",
        [0x0057] = "RecognizedTextWord",
    };

    public static string For(ushort index) =>
        ByIndex.TryGetValue(index, out var name)
            ? name
            : "0x" + index.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
}
