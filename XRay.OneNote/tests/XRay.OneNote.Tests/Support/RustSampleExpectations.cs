namespace XRay.OneNote.Tests.Support;

/// <summary>
/// What an independent parser (run as an oracle, never copied) reports for the optional third-party samples:
/// identity GUID, section name, page levels in order and the title of each page. Used by tests that skip
/// unless XRAY_ONENOTE_RS_SAMPLES names the samples folder.
/// </summary>
internal static class RustSampleExpectations
{
    public sealed record Expected(string Path, string Identity, string DisplayName, int[] Levels, string[] Titles);

    public static readonly Expected[] All =
    [
        new("New Section 1.one", "0575dd0a-5612-d746-b82b-983f7a80b282", "New Section 1", [1], ["Test Page"]),
        new("New Section Group/New Section 1.one", "07e1868e-e7d0-c24e-85c6-7cbe04486764", "New Section 1", [1], ["Test Page 2"]),
        new("New Section Group/New Section 2.one", "18481a4a-6857-0748-ac96-6327c4ada7c0", "New Section 2", [1, 1], ["Test Page 3", "Test Page 4"]),
        new("OneNote_RecycleBin/OneNote_DeletedPages.one", "bee9d753-c3df-804e-961b-c38c9754244b", "OneNote_DeletedPages", [1], ["Te"]),
        new("Schnelle Notizen.one", "4979bebe-eeaa-4bd1-ab0d-75ef7cf59533", "Scribbles", [1], ["Note"]),
        new("handwriting_recognition.one", "3ef70c2a-1787-854a-a0b4-ef192956c142", "handwriting_recognition", [1, 1], ["Test Page", "Test Page"]),
        new("joplin/Math.one", "de6d9757-5184-498e-875c-95458f1a9129", "Math", [1], ["Math"]),
        new("joplin/Notebook created on OneNote App/OneNote_RecycleBin/OneNote_DeletedPages.one", "0e1acfd7-d99f-4f9e-ba8a-e06f35c31c33", "OneNote_DeletedPages", [1, 1], ["Created on OneNote App", "asdfasf"]),
        new("joplin/Notebook created on OneNote App/Section A/Section A1.one", "585242e1-06e9-46bb-89d5-dc2e10660eb9", "Section A1", [1, 1], ["Section A note 2", "Section A note 1"]),
        new("joplin/Notebook created on OneNote App/Section A/Section B/Section B1.one", "c81591f9-2b86-42f4-b9f0-faa9a9091387", "Section B1", [1, 1], ["Section B1 page 1", "Section B1 page 2"]),
        new("joplin/Notebook created on OneNote App/Section D/Section D1.one", "d65d2508-39c4-41a8-b519-eb4a4a723df1", "Section D1", [1], ["Section D1 page"]),
        new("joplin/Notebook created on OneNote App/Section.one", "c9243001-fb54-4c90-b917-5bb4b43186af", "Section", [1], ["First note"]),
        new("joplin/Notebook with subsections and subpages/Section 1.one", "8c3c360f-0cb3-4a6a-94d5-037cc9628114", "Section", [1, 2, 2, 1, 2, 3, 3, 2, 3], ["Page 1", "Page 1-a", "Page 1-b", "Page 2", "Page 2-a", "Page 2-a-a", "Page 2-a-b", "Page 2-b", "Page 2-b-a"]),
        new("joplin/Second/Group Section 1/Group Section 1-a/Subsection 3.one", "8e616d51-5959-4735-92ae-6b6c592b674c", "Subsection 3", [1], ["Subsection 3 page page"]),
        new("joplin/Second/Group Section 1/Subsection 1.one", "f379cd27-fab8-4b36-bb97-ce7d99bc7240", "Subsection 1", [1], ["Subscetion 1 page"]),
        new("joplin/Second/Group Section 1/Subsection 2.one", "b173c4a2-12b5-4efc-b81f-0e7001e20569", "Subsection 2", [1], ["Subsection 2 page"]),
        new("joplin/Simple notebook/Quick Notes.one", "85d60ea8-0222-4087-8987-216706bc4f44", "Section title", [1], ["Page title"]),
        new("joplin/aaa/OneNote_RecycleBin/OneNote_DeletedPages.one", "383fa6a5-8abd-40f3-a873-0699c71fe098", "OneNote_DeletedPages", [1], ["asdf"]),
        new("joplin/aaa/Quick Notes.one", "075abba1-d70d-4ce7-a0de-0b2da37f0aa9", "Quick Notes", [1, 1, 1, 1, 1, 1], ["A simple filename", "A page with a lot of svgs", "A page can have any width it wants?", "A page with text and drawing above it", "text", "Page with more than one font size"]),
        new("joplin/audio-test/Quick Notes.one", "0b5dac4f-f613-4dc3-9312-f722cccfb433", "Quick Notes", [1], ["My title"]),
        new("joplin/checkboxes_and_unicode.one", "203c2193-d88d-46fd-994d-124ec928b99b", "checkboxes_and_unicode", [1, 1, 1, 1], ["Section 2 - simple page 1 de test", "Section 2 - avec titre accentué et caractères spéciaux &é\"'(-è_çà)=~#{[|`\\^@]}", "Test niveau de titre et liens web", "Test Todo : cases à cocher lien vers doc sur partage"]),
        new("joplin/default-edited/a.one", "7d8f45f9-c981-439c-8b5c-0169a020831b", "a", [1], ["Action research - Wikipedia"]),
        new("joplin/desktop_missing_ink.one", "602d9213-3b80-47da-b8b7-bb4218dbfa22", "desktop_missing_ink", [1, 1], ["Ink Missing - among other ink objects", "Ink Missing - only one example missing part"]),
        new("joplin/hyperlink_is_broken/Quick Notes.one", "d5146f44-3f84-46e9-97f9-fc61c55ff0c8", "Quick Notes", [1], ["Is Mexico safe for shooting Street Photography?"]),
        new("joplin/new_section.one", "95adb014-6588-4d92-8079-3e2fff734249", "new_section", [1], ["title"]),
        new("joplin/notebook_with_chinese_char_on_link/Quick Notes.one", "7ea640c0-af2b-4eff-ab18-24e1d84c1bdc", "Quick Notes", [1, 1, 1, 1], ["风景  (Web view)", "wikipedia link", "风景", "Tips from a Pro: Using Trees for Dramatic Landscape Photography"]),
        new("joplin/onenote_desktop.one", "dd5d3d82-4db1-45c3-af8b-14d439cf225a", "onenote_desktop", [1, 1, 1], ["Test", "Another page", "Page 3"]),
        new("joplin/scaled_ink.one", "289df1eb-6156-4b7f-b782-94e65c318e4b", "scaled_ink", [1], ["Scaled"]),
        new("non-legacy/New Section 1 2.one", "26a89915-6861-9b44-856a-b6296cf894af", "New Section 1 2", [1, 1], ["Test Page", "Test Page"]),
        new("non-legacy/New Section 2.one", "11b448eb-ebf3-4d0d-9347-6a15fdea7f08", "New Section 2", [1, 1], ["", ""]),
        new("non-legacy/New Section 3.one", "760b77af-cc00-a44a-9f63-8bb5350e12a0", "New Section 3", [1], [""]),
        new("onenote-2016/OneWithFileData.one", "af6de84b-d817-4f07-8baf-fb6629cb7c2c", "OneWithFileData", [1], [""]),
    ];
}
