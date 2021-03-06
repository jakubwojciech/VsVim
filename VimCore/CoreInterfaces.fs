﻿#light

namespace Vim
open EditorUtils
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Text.Outlining
open Microsoft.VisualStudio.Utilities
open System.Diagnostics
open System.Runtime.CompilerServices

type TextViewEventArgs(_textView : ITextView) =
    inherit System.EventArgs()

    member x.TextView = _textView

[<RequireQualifiedAccess>]
type HostResult =
    | Success
    | Error of string

[<RequireQualifiedAccess>]
type SelectionKind =
    | Inclusive
    | Exclusive

[<RequireQualifiedAccess>]
type JoinKind = 
    | RemoveEmptySpaces
    | KeepEmptySpaces

[<RequireQualifiedAccess>]
type ChangeCharacterKind =
    /// Switch the characters to upper case
    | ToUpperCase

    /// Switch the characters to lower case
    | ToLowerCase

    /// Toggle the case of the characters
    | ToggleCase

    /// Rot13 encode the letters
    | Rot13

/// Map containing the various VIM registers
type IRegisterMap = 

    /// Gets all of the available register name values
    abstract RegisterNames : seq<RegisterName>

    /// Get the register with the specified name
    abstract GetRegister : RegisterName -> Register

    /// Update the register with the specified value
    abstract SetRegisterValue : Register -> RegisterOperation -> RegisterValue -> unit

type IStatusUtil =

    /// Raised when there is a special status message that needs to be reported
    abstract OnStatus : string -> unit

    /// Raised when there is a long status message that needs to be reported
    abstract OnStatusLong : string seq -> unit 

    /// Raised when there is an error message that needs to be reported
    abstract OnError : string -> unit 

    /// Raised when there is a warning message that needs to be reported
    abstract OnWarning : string -> unit 

/// Factory for getting IStatusUtil instances.  This is an importable MEF component
type IStatusUtilFactory =

    /// Get the IStatusUtil instance for the given ITextView
    abstract GetStatusUtil : ITextView -> IStatusUtil

type FileContents = {

    /// Full path to the file which the contents were loaded from
    FilePath : string

    /// Actual lines in the file
    Lines : string[]
}

/// Abstracts away VsVim's interaction with the file system to facilitate testing
type IFileSystem =

    /// Set of environment variables considered when looking for VimRC paths
    abstract EnvironmentVariables : list<string>

    /// Set of file names considered (in preference order) when looking for vim rc files
    abstract VimRcFileNames : list<string>
    
    /// Get the directories to probe for RC files
    abstract GetVimRcDirectories : unit -> seq<string>

    /// Get the file paths in preference order for vim rc files
    abstract GetVimRcFilePaths : unit -> seq<string>

    /// Attempts to load the contents of the .VimRC and return both the path the file
    /// was loaded from and it's contents a
    abstract LoadVimRcContents : unit -> FileContents option

    /// Attempt to read all of the lines from the given file 
    abstract ReadAllLines : filePath : string -> string[] option

/// Utility functions relating to Word values in an ITextBuffer
type IWordUtil = 

    /// The ITextBuffer associated with this word utility
    abstract TextBuffer : ITextBuffer

    /// Get the full word span for the word value which crosses the given SnapshotPoint
    abstract GetFullWordSpan : WordKind -> SnapshotPoint -> SnapshotSpan option

    /// Get the SnapshotSpan for Word values from the given point.  If the provided point is 
    /// in the middle of a word the span of the entire word will be returned
    abstract GetWords : WordKind -> Path -> SnapshotPoint -> SnapshotSpan seq

    /// Create an ITextStructureNavigator where the extent of words is calculated for
    /// the specified WordKind value
    abstract CreateTextStructureNavigator : WordKind -> ITextStructureNavigator

/// Factory for getting IWordUtil instances.  This is an importable MEF component
type IWordUtilFactory = 

    /// Get the IWordUtil instance for the given ITextView
    abstract GetWordUtil : ITextBuffer -> IWordUtil

/// Used to display a word completion list to the user
type IWordCompletionSession =

    /// Is the session dismissed
    abstract IsDismissed : bool

    /// The associated ITextView instance
    abstract TextView : ITextView

    /// Select the next word in the session
    abstract MoveNext : unit -> bool

    /// Select the previous word in the session.
    abstract MovePrevious : unit -> bool

    /// Dismiss the completion session 
    abstract Dismiss : unit -> unit

    /// Raised when the session is dismissed
    [<CLIEvent>]
    abstract Dismissed: IDelegateEvent<System.EventHandler>

/// Factory service for creating IWordCompletionSession instances
type IWordCompletionSessionFactoryService = 

    /// Create a session with the given set of words
    abstract CreateWordCompletionSession : textView : ITextView -> wordSpan : SnapshotSpan -> words : string seq -> isForward : bool -> IWordCompletionSession

/// Wraps an ITextUndoTransaction so we can avoid all of the null checks
type IUndoTransaction =

    /// Adds an ITextUndoPrimitive which will reset the selection to the current
    /// state when redoing this edit
    abstract AddAfterTextBufferChangePrimitive : unit -> unit

    /// Adds an ITextUndoPrimitive which will reset the selection to the current
    /// state when undoing this change
    abstract AddBeforeTextBufferChangePrimitive : unit -> unit

    /// Call when it completes
    abstract Complete : unit -> unit

    /// Cancels the transaction
    abstract Cancel : unit -> unit

    inherit System.IDisposable

/// Wraps a set of IUndoTransaction items such that they undo and redo as a single
/// entity.
type ILinkedUndoTransaction =

    /// Complete the linked operation
    abstract Complete : unit -> unit

    inherit System.IDisposable

/// Wraps all of the undo and redo operations
type IUndoRedoOperations = 

    /// Is there an open linked undo transaction
    abstract InLinkedUndoTransaction : bool

    /// StatusUtil instance that is used to report errors
    abstract StatusUtil : IStatusUtil

    /// Close the IUndoRedoOperations and remove any attached event handlers
    abstract Close : unit -> unit

    /// Creates an Undo Transaction
    abstract CreateUndoTransaction : name:string -> IUndoTransaction

    /// Creates a linked undo transaction
    abstract CreateLinkedUndoTransaction : unit -> ILinkedUndoTransaction

    /// Wrap the passed in "action" inside an undo transaction.  This is needed
    /// when making edits such as paste so that the cursor will move properly 
    /// during an undo operation
    abstract EditWithUndoTransaction<'T> : name : string -> action : (unit -> 'T) -> 'T

    /// Redo the last "count" operations
    abstract Redo : count:int -> unit

    /// Undo the last "count" operations
    abstract Undo : count:int -> unit

/// Represents a set of changes to a contiguous region. 
[<RequireQualifiedAccess>]
type TextChange = 
    | DeleteLeft of int
    | DeleteRight of int
    | Insert of string
    | Combination of TextChange * TextChange

    with 

    /// Get the insert text resulting from the change if there is any
    member x.InsertText = 
        let rec inner textChange (text : string) = 
            match textChange with 
            | Insert data -> text + data |> Some
            | DeleteLeft count -> 
                if count > text.Length then
                    None
                else 
                    text.Substring(0, text.Length - count) |> Some
            | DeleteRight _ -> None
            | Combination (left, right) ->
                match inner left text with
                | None -> None
                | Some text -> inner right text

        inner x StringUtil.empty

    /// Get the last / most recent change in the TextChange tree
    member x.LastChange = 
        match x with
        | DeleteLeft _ -> x
        | DeleteRight _ -> x
        | Insert _ -> x
        | Combination (_, right) -> right.LastChange

    /// Merge two TextChange values together.  The goal is to produce a the smallest TextChange
    /// value possible
    static member Merge left right =

        let noMerge () = Combination (left, right)

        let mergeInsert (text : string) =
            match right with
            | DeleteLeft count -> 
                if count > text.Length then
                    DeleteLeft (count - text.Length)
                else
                    let text = text.Substring(0, text.Length - count)
                    Insert text
            | DeleteRight count -> noMerge ()
            | Insert otherText -> Insert (text + otherText)
            | Combination _ -> noMerge ()

        let mergeDeleteLeft count = 
            match right with
            | DeleteLeft otherCount -> DeleteLeft (count + otherCount)
            | DeleteRight _ -> noMerge ()
            | Insert _ -> noMerge ()
            | Combination _ -> noMerge()

        let mergeDeleteRight count = 
            match right with
            | DeleteRight otherCount -> DeleteRight (count + otherCount)
            | DeleteLeft _ -> noMerge ()
            | Insert _ -> noMerge ()
            | Combination _ -> noMerge()

        match left with
        | Insert text -> mergeInsert text
        | DeleteLeft count -> mergeDeleteLeft count
        | DeleteRight count -> mergeDeleteRight count
        | Combination _ -> noMerge()

    static member Replace str =
        let left = str |> StringUtil.length |> TextChange.DeleteLeft
        let right = TextChange.Insert str
        TextChange.Combination (left, right)

type TextChangeEventArgs(_textChange : TextChange) =
    inherit System.EventArgs()

    member x.TextChange = _textChange

[<System.Flags>]
type SearchOptions = 
    | None = 0x0

    /// Consider the "ignorecase" option when doing the search
    | ConsiderIgnoreCase = 0x1

    /// Consider the "smartcase" option when doing the search
    | ConsiderSmartCase = 0x2

/// Information about a search of a pattern
type PatternData = {

    /// The Pattern to search for
    Pattern : string

    /// The direction in which the pattern was searched for
    Path : Path
}
    with 

    /// The default search options when looking at a specific pattern
    static member DefaultSearchOptions = SearchOptions.ConsiderIgnoreCase ||| SearchOptions.ConsiderSmartCase

type PatternDataEventArgs(_patternData : PatternData) =
    inherit System.EventArgs()

    member x.PatternData = _patternData

type SearchData = {

    /// The pattern being searched for in the buffer
    Pattern : string

    Kind : SearchKind;

    Options : SearchOptions
} with

    member x.PatternData = { Pattern = x.Pattern; Path = x.Kind.Path }

    static member OfPatternData (patternData : PatternData) wrap = 
        {
            Pattern = patternData.Pattern
            Kind = SearchKind.OfPathAndWrap patternData.Path wrap
            Options = PatternData.DefaultSearchOptions
        }

type SearchDataEventArgs(_searchData : SearchData) =
    inherit System.EventArgs()

    member x.SearchData = _searchData

/// Result of an individual search
[<RequireQualifiedAccess>]
type SearchResult =

    /// The pattern was found.  The bool at the end of the tuple represents whether not
    /// a wrap occurred while searching for the value
    | Found of SearchData * SnapshotSpan * bool

    /// The pattern was not found.  The bool is true if the word was present in the ITextBuffer
    /// but wasn't found do to the lack of a wrap in the SearchData value
    | NotFound of SearchData * bool

    with

    /// Returns the SearchData which was searched for
    member x.SearchData = 
        match x with 
        | SearchResult.Found (searchData, _, _) -> searchData
        | SearchResult.NotFound (searchData, _) -> searchData

type SearchResultEventArgs(_searchResult : SearchResult) = 
    inherit System.EventArgs()

    member x.SearchResult = _searchResult

/// Global information about searches within Vim.  
///
/// This interface is usable from any thread
[<UsedInBackgroundThread()>]
type ISearchService = 

    /// Find the next occurrence of the pattern in the buffer starting at the 
    /// given SnapshotPoint
    abstract FindNext : SearchData -> SnapshotPoint -> ITextStructureNavigator -> SearchResult

    /// Find the next Nth occurrence of the search data
    abstract FindNextMultiple : SearchData -> SnapshotPoint -> ITextStructureNavigator -> count:int -> SearchResult

    /// Find the next 'count' occurrence of the specified pattern.  Note: The first occurrence won't
    /// match anything at the provided start point.  That will be adjusted appropriately
    abstract FindNextPattern : PatternData -> SnapshotPoint -> ITextStructureNavigator -> count : int -> SearchResult

/// Column information about the caret in relation to this Motion Result
[<RequireQualifiedAccess>]
type CaretColumn = 

    /// No column information was provided
    | None

    /// Caret should be placed in the specified column on the last line in 
    /// the MotionResult
    ///
    /// This column should be specified in terms of a character offset in the ITextBuffer
    /// and shouldn't consider items like how wide a tab is.  A tab should be a single
    /// character
    | InLastLine of int

    /// Caret should be placed at the start of the line after the last line
    /// in the motion
    | AfterLastLine

/// These are the types of motions which must be handled separately
[<RequireQualifiedAccess>]
type MotionResultFlags = 

    /// No special information for this motion
    | None = 0

    /// Any of the word motions
    | AnyWord = 0x1

    /// This motion was promoted under rule #2 to a line wise motion
    | ExclusiveLineWise = 0x2

    /// This motion when used as a movement should maintain the caret column
    /// setting.
    | MaintainCaretColumn = 0x4

    /// This flag is needed to disambiguate the cases which come up when there
    /// is an empty last line in the buffer.  When that happens it's ambiguous 
    /// if a line wise motion meant to include the last line or merely just 
    /// the line above.  The End is the same in both cases.
    | IncludeEmptyLastLine = 0x8

    /// Marker for the end of line motion.  This affects how the caret column
    /// is maintained
    | EndOfLine = 0x10

/// Information about the type of the motion this was.
[<RequireQualifiedAccess>]
type MotionKind =

    | CharacterWiseInclusive

    | CharacterWiseExclusive

    /// In addition to recording the Span certain line wise operations like j and k also
    /// record data about the desired column within the span.  This value may or may not
    /// be a valid point within the line
    | LineWise of CaretColumn

/// Data about a complete motion operation. 
type MotionResult = {

    /// Span of the motion.
    Span : SnapshotSpan

    /// In the case this MotionResult is the result of an exclusive promotion, this will 
    /// hold the original SnapshotSpan
    OriginalSpan : SnapshotSpan

    /// Is the motion forward
    IsForward : bool

    /// Kind of the motion
    MotionKind : MotionKind

    /// The flags on the MotionRelult
    MotionResultFlags : MotionResultFlags

} with

    /// The possible column of the MotionResult
    member x.CaretColumn = 
        match x.MotionKind with
        | MotionKind.CharacterWiseExclusive -> CaretColumn.None
        | MotionKind.CharacterWiseInclusive -> CaretColumn.None
        | MotionKind.LineWise column -> column

    /// The Span as an EditSpan value
    member x.EditSpan = EditSpan.Single x.Span

    /// The OperationKind of the MotionResult
    member x.OperationKind = 
        match x.MotionKind with
        | MotionKind.CharacterWiseExclusive -> OperationKind.CharacterWise
        | MotionKind.CharacterWiseInclusive -> OperationKind.CharacterWise
        | MotionKind.LineWise _ -> OperationKind.LineWise

    /// Is this a word motion 
    member x.IsAnyWordMotion = Util.IsFlagSet x.MotionResultFlags MotionResultFlags.AnyWord

    /// Is this an exclusive motion
    member x.IsExclusive =
        match x.MotionKind with
        | MotionKind.CharacterWiseExclusive -> true
        | MotionKind.CharacterWiseInclusive -> false
        | MotionKind.LineWise _ -> false

    /// Is this an inclusive motion
    member x.IsInclusive = not x.IsExclusive

    /// The Span as a SnapshotLineRange value 
    member x.LineRange = SnapshotLineRangeUtil.CreateForSpan x.Span

    /// The Start or Last line depending on whether tho motion is forward or not
    member x.DirectionLastLine = 
        if x.IsForward then
            // Need to handle the empty last line case here.  If the flag to include
            // the empty last line is set and we are at the empty line then it gets
            // included as the last line
            if SnapshotPointUtil.IsEndPoint x.Span.End && Util.IsFlagSet x.MotionResultFlags MotionResultFlags.IncludeEmptyLastLine then
                SnapshotPointUtil.GetContainingLine x.Span.End
            else
                SnapshotSpanUtil.GetLastLine x.Span
        else
            SnapshotSpanUtil.GetStartLine x.Span

    static member CreateEx span isForward motionKind motionResultFlags = 
        {
            Span = span
            OriginalSpan = span
            IsForward = isForward
            MotionKind = motionKind
            MotionResultFlags = motionResultFlags }

    static member Create span isForward motionKind = MotionResult.CreateEx span isForward motionKind MotionResultFlags.None

/// Context on how the motion is being used.  Several motions (]] for example)
/// change behavior based on how they are being used
[<RequireQualifiedAccess>]
type MotionContext =
    | Movement
    | AfterOperator

/// Arguments necessary to building a Motion
type MotionArgument = {

    /// Context of the Motion
    MotionContext : MotionContext

    /// Count passed to the operator
    OperatorCount : int option

    /// Count passed to the motion 
    MotionCount : int option 

} with

    /// Provides the raw count which is a combination of the OperatorCount
    /// and MotionCount values.  
    member x.RawCount = 
        match x.MotionCount,x.OperatorCount with
        | None,None -> None
        | Some(c),None -> Some c
        | None,Some(c) -> Some c
        | Some(l),Some(r) -> Some (l*r)

    /// Resolves the count to a value.  It will use the default 1 for 
    /// any count which is not provided 
    member x.Count = 
        let operatorCount = x.OperatorCount |> OptionUtil.getOrDefault 1
        let motionCount = x.MotionCount |> OptionUtil.getOrDefault 1
        operatorCount * motionCount

/// Char searches are interesting because they are definide in one IVimBuffer
/// and can be repeated in any IVimBuffer.  Use a discriminated union here 
/// to name the motion without tieing it to a given IVimBuffer or ITextView 
/// which would increase the chance of an accidental memory leak
[<RequireQualifiedAccess>]
type CharSearchKind  =

    /// Used for the 'f' and 'F' motion.  To the specified char 
    | ToChar

    /// Used for the 't' and 'T' motion.  Till the specified char
    | TillChar

[<RequireQualifiedAccess>]
type BlockKind =

    /// A [] block
    | Bracket 

    /// A () block
    | Paren

    /// A <> block
    | AngleBracket

    /// A {} block
    | CurlyBracket

    with

    member x.Characters = 
        match x with 
        | Bracket -> '[', ']'
        | Paren -> '(', ')'
        | AngleBracket -> '<', '>'
        | CurlyBracket -> '{', '}'

/// A discriminated union of the Motion types supported.  These are the primary
/// repeat mechanisms for Motion arguments so it's very important that these 
/// are ITextView / IVimBuffer agnostic.  It will be very common for a Motion 
/// item to be stored and then applied to many IVimBuffer instances.
[<RequireQualifiedAccess>]
type Motion =

    /// Implement the all block motion
    | AllBlock of BlockKind

    /// Implement the 'aw' motion.  This is called once the a key is seen.
    | AllWord of WordKind

    /// Implement the 'ap' motion
    | AllParagraph 

    /// Gets count full sentences from the cursor.  If used on a blank line this will
    /// not return a value
    | AllSentence

    /// Move to the begining of the line.  Interestingly since this command is bound to the '0' it 
    /// can't be associated with a count.  Doing a command like 30 binds as count 30 vs. count 3 
    /// for command '0'
    | BeginingOfLine

    /// The left motion for h, <Left>, etc ...
    | CharLeft 

    /// The right motion for l, <Right>, etc ...
    | CharRight

    /// Implements the f, F, t and T motions
    | CharSearch of CharSearchKind * Path * char

    /// Get the span of "count" display lines upward.  Display lines can differ when
    /// wrap is enabled
    | DisplayLineUp

    /// Get the span of "count" display lines downward.  Display lines can differ when
    /// wrap is enabled
    | DisplayLineDown

    /// Implement the 'e' motion.  This goes to the end of the current word.  If we're
    /// not currently on a word it will find the next word and then go to the end of that
    | EndOfWord of WordKind
    
    /// Implement an end of line motion.  Typically in response to the $ key.  Even though
    /// this motion deals with lines, it's still a character wise motion motion. 
    | EndOfLine

    /// Find the first non-blank character as the start of the span.  This is an exclusive
    /// motion so be careful we don't go to far forward.  Providing a count to this motion has
    /// no affect
    | FirstNonBlankOnCurrentLine

    /// Find the first non-blank character on the (count - 1) line below this line
    | FirstNonBlankOnLine

    /// Inner word motion
    | InnerWord of WordKind

    /// Inner block motion
    | InnerBlock of BlockKind

    /// Find the last non-blank character on the line.  Count causes it to go "count" lines
    /// down and perform the search
    | LastNonBlankOnLine

    /// Find the next occurrence of the last search.  The bool parameter is true if the search
    /// should be in the opposite direction
    | LastSearch of bool

    /// Handle the lines down to first non-blank motion.  This is one of the motions which 
    /// can accept a count of 0.
    | LineDownToFirstNonBlank

    /// Handle the - motion
    | LineUpToFirstNonBlank

    /// Get the span of "count" lines upward careful not to run off the beginning of the
    /// buffer.  Implementation of the "k" motion
    | LineUp

    /// Get the span of "count" lines downward careful not to run off the end of the
    /// buffer.  Implementation of the "j" motion
    | LineDown

    /// Go to the specified line number or the first line if no line number is provided 
    | LineOrFirstToFirstNonBlank

    /// Go to the specified line number or the last line of no line number is provided
    | LineOrLastToFirstNonBlank

    /// Go to the "count - 1" line from the top of the visible window.  If the count exceeds
    /// the number of visible lines it will end on the last visible line
    | LineFromTopOfVisibleWindow

    /// Go to the "count -1" line from the bottom of the visible window.  If the count 
    /// exceeds the number of visible lines it will end on the first visible line
    | LineFromBottomOfVisibleWindow

    /// Go to the middle line in the visible window.  
    | LineInMiddleOfVisibleWindow

    /// Get the motion to the specified mark.  This is typically accessed via
    /// the ` (backtick) operator and results in an exclusive motion
    | Mark of LocalMark

    /// Get the motion to the line of the specified mark.  This is typically
    /// accessed via the ' (single quote) operator and results in a 
    /// linewise motion
    | MarkLine of LocalMark

    /// Get the matching token from the next token on the line.  This is used to implement
    /// the % motion
    | MatchingToken 

    /// Search for the next occurrence of the word under the caret
    | NextWord of Path

    /// Search for the next partial occurrence of the word under the caret
    | NextPartialWord of Path

    /// Count paragraphs backwards
    | ParagraphBackward

    /// Count paragraphs forward
    | ParagraphForward

    /// The quoted string including the quotes
    | QuotedString of char

    /// The quoted string excluding the quotes
    | QuotedStringContents of char

    /// Repeat the last CharSearch value
    | RepeatLastCharSearch

    /// Repeat the last CharSearch value in the opposite direction
    | RepeatLastCharSearchOpposite

    /// A search for the specified pattern
    | Search of PatternData

    /// Backward a section in the editor or to a close brace
    | SectionBackwardOrCloseBrace
    
    /// Backward a section in the editor or to an open brace
    | SectionBackwardOrOpenBrace

    /// Forward a section in the editor or to a close brace
    | SectionForwardOrCloseBrace

    /// Forward a section in the editor
    | SectionForward

    /// Count sentences backward 
    | SentenceBackward

    /// Count sentences forward
    | SentenceForward

    /// Implement the b/B motion
    | WordBackward of WordKind

    /// Implement the w/W motion
    | WordForward of WordKind 

/// Interface for running Motion instances against an ITextView
and IMotionUtil =

    /// The associated ITextView instance
    abstract TextView : ITextView

    /// Get the specified Motion value 
    abstract GetMotion : motion : Motion -> motionArgument : MotionArgument -> MotionResult option 

    /// Get the specific text object motion from the given SnapshotPoint
    abstract GetTextObject : motion : Motion -> point : SnapshotPoint -> MotionResult option

type ModeKind = 
    | Normal = 1
    | Insert = 2
    | Command = 3
    | VisualCharacter = 4
    | VisualLine = 5
    | VisualBlock = 6 
    | Replace = 7
    | SubstituteConfirm = 8
    | Select = 9
    | ExternalEdit = 10

    /// Initial mode for an IVimBuffer.  It will maintain this mode until the underyling
    /// ITextView completes it's initialization and allows the IVimBuffer to properly 
    /// transition to the mode matching it's underlying IVimTextBuffer
    | Uninitialized = 11

    /// Mode when Vim is disabled.  It won't interact with events it otherwise would such
    /// as selection changes
    | Disabled = 42

[<RequireQualifiedAccess>]
type VisualKind =
    | Character
    | Line
    | Block
    with 

    /// The TextSelectionMode this VisualKind would require
    member x.TextSelectionMode = 
        match x with
        | Character _ -> TextSelectionMode.Stream
        | Line _ -> TextSelectionMode.Stream
        | Block _ -> TextSelectionMode.Box

    member x.ModeKind = 
        match x with
        | Character _ -> ModeKind.VisualCharacter
        | Line _ -> ModeKind.VisualLine
        | Block _ -> ModeKind.VisualBlock

    static member All = [ Character; Line; Block ] |> Seq.ofList

    static member OfModeKind kind = 
        match kind with 
        | ModeKind.VisualBlock -> VisualKind.Block |> Some
        | ModeKind.VisualLine -> VisualKind.Line |> Some
        | ModeKind.VisualCharacter -> VisualKind.Character |> Some
        | _ -> None

    static member IsAnyVisual kind = VisualKind.OfModeKind kind |> Option.isSome

    static member IsAnyVisualOrSelect kind = VisualKind.IsAnyVisual kind || kind = ModeKind.Select

/// The actual command name.  This is a wrapper over the collection of KeyInput 
/// values which make up a command name.  
///
/// The intent of this type is that two values are equal if the sequence of 
/// KeyInputs are Equal.  So a OneKeyInput can be equal to a ManyKeyInputs if the
/// have the same values
///
/// It is not possible to simple store this as a string as it is possible, and 
/// in fact likely due to certain virtual key codes which are unable to be mapped,
/// for KeyInput values will map to a single char.  Hence to maintain proper semantics
/// we have to use KeyInput values directly.
[<CustomEquality; CustomComparison>]
[<DebuggerDisplay("{ToString(),nq}")>]
type KeyInputSet =
    | Empty
    | OneKeyInput of KeyInput
    | TwoKeyInputs of KeyInput * KeyInput
    | ManyKeyInputs of KeyInput list
    with 

    /// Returns the first KeyInput if present
    member x.FirstKeyInput = 
        match x with 
        | Empty -> None
        | OneKeyInput(ki) -> Some ki
        | TwoKeyInputs(ki,_) -> Some ki
        | ManyKeyInputs(list) -> ListUtil.tryHeadOnly list

    /// Returns the rest of the KeyInput values after the first
    member x.Rest = 
        match x with
        | Empty -> List.empty
        | OneKeyInput _ -> List.empty
        | TwoKeyInputs (_, keyInput2) -> [ keyInput2 ]
        | ManyKeyInputs list -> List.tail list

    /// Get the list of KeyInput which represent this KeyInputSet
    member x.KeyInputs =
        match x with 
        | Empty -> List.empty
        | OneKeyInput(ki) -> [ki]
        | TwoKeyInputs(k1,k2) -> [k1;k2]
        | ManyKeyInputs(list) -> list

    /// A string representation of the name.  It is unreliable to use this for anything
    /// other than display as two distinct KeyInput values can map to a single char
    member x.Name = x.KeyInputs |> Seq.map (fun ki -> ki.Char) |> StringUtil.ofCharSeq

    /// Length of the contained KeyInput's
    member x.Length =
        match x with
        | Empty -> 0
        | OneKeyInput _ -> 1
        | TwoKeyInputs _ -> 2
        | ManyKeyInputs list -> list.Length

    /// Add a KeyInput to the end of this KeyInputSet and return the 
    /// resulting value
    member x.Add ki =
        match x with 
        | Empty -> OneKeyInput ki
        | OneKeyInput previous -> TwoKeyInputs(previous,ki)
        | TwoKeyInputs (p1, p2) -> ManyKeyInputs [p1;p2;ki]
        | ManyKeyInputs list -> ManyKeyInputs (list @ [ki])

    /// Does the name start with the given KeyInputSet
    member x.StartsWith (targetName : KeyInputSet) = 
        match targetName,x with
        | Empty, _ -> true
        | OneKeyInput leftKi, OneKeyInput rightKi ->  leftKi = rightKi
        | OneKeyInput leftKi, TwoKeyInputs (rightKi, _) -> leftKi = rightKi
        | _ -> 
            let left = targetName.KeyInputs 
            let right = x.KeyInputs
            if left.Length <= right.Length then
                SeqUtil.contentsEqual (left |> Seq.ofList) (right |> Seq.ofList |> Seq.take left.Length)
            else false

    member x.CompareTo (other : KeyInputSet) = 
        let rec inner (left:KeyInput list) (right:KeyInput list) =
            if left.IsEmpty && right.IsEmpty then 0
            elif left.IsEmpty then -1
            elif right.IsEmpty then 1
            elif left.Head < right.Head then -1
            elif left.Head > right.Head then 1
            else inner (List.tail left) (List.tail right)
        inner x.KeyInputs other.KeyInputs

    override x.GetHashCode() = 
        match x with
        | Empty -> 1
        | OneKeyInput ki -> ki.GetHashCode()
        | TwoKeyInputs (k1, k2) -> k1.GetHashCode() ^^^ k2.GetHashCode()
        | ManyKeyInputs list -> 
            list 
            |> Seq.ofList
            |> Seq.map (fun ki -> ki.GetHashCode())
            |> Seq.sum

    override x.Equals(yobj) =
        match yobj with
        | :? KeyInputSet as y -> 
            match x,y with
            | OneKeyInput(left),OneKeyInput(right) -> left = right
            | TwoKeyInputs(l1,l2),TwoKeyInputs(r1,r2) -> l1 = r1 && l2 = r2
            | _ -> ListUtil.contentsEqual x.KeyInputs y.KeyInputs
        | _ -> false

    static member op_Equality(this,other) = System.Collections.Generic.EqualityComparer<KeyInputSet>.Default.Equals(this,other)
    static member op_Inequality(this,other) = not (System.Collections.Generic.EqualityComparer<KeyInputSet>.Default.Equals(this,other))

    override x.ToString() =
        x.KeyInputs
        |> Seq.map (fun ki ->
            if ki.Key = VimKey.RawCharacter then ki.Char.ToString()
            elif ki.Key = VimKey.None then "<None>"
            else System.String.Format("<{0}>", ki.Key)  )
        |> StringUtil.ofStringSeq

    interface System.IComparable with
        member x.CompareTo yobj = 
            match yobj with
            | :? KeyInputSet as y -> x.CompareTo y
            | _ -> failwith "Cannot compare values of different types"

module KeyInputSetUtil =

    let OfSeq sequence = 
        match Seq.length sequence with
        | 0 -> KeyInputSet.Empty
        | 1 -> KeyInputSet.OneKeyInput (Seq.nth 0 sequence)
        | 2 -> KeyInputSet.TwoKeyInputs ((Seq.nth 0 sequence),(Seq.nth 1 sequence))
        | _ -> sequence |> List.ofSeq |> KeyInputSet.ManyKeyInputs 

    let OfList list = 
        match list with
        | [] -> KeyInputSet.Empty
        | [ki] -> KeyInputSet.OneKeyInput ki
        | _ -> 
            match list.Length with
            | 2 -> KeyInputSet.TwoKeyInputs ((List.nth list 0),(List.nth list 1))
            | _ -> KeyInputSet.ManyKeyInputs list

    let OfChar c = c |> KeyInputUtil.CharToKeyInput |> OneKeyInput

    let OfString (str:string) = str |> Seq.map KeyInputUtil.CharToKeyInput |> OfSeq

    let OfVimKeyArray ([<System.ParamArray>] arr) = 
        arr 
        |> Seq.ofArray 
        |> Seq.map KeyInputUtil.VimKeyToKeyInput
        |> OfSeq

    let Combine (left : KeyInputSet) (right : KeyInputSet) =
        let all = left.KeyInputs @ right.KeyInputs
        OfList all

/// Modes for a key remapping
[<RequireQualifiedAccess>]
[<DebuggerDisplay("{ToString(),nq}")>]
type KeyRemapMode =
    | Normal 
    | Visual 
    | Select 
    | OperatorPending 
    | Insert 
    | Command 
    | Language 

    with 

    static member All = 
        seq {
            yield Normal
            yield Visual 
            yield Select
            yield OperatorPending
            yield Insert
            yield Command
            yield Language }

    override x.ToString() =
        match x with 
        | Normal -> "Normal"
        | Visual -> "Visual"
        | Select -> "Select"
        | OperatorPending -> "OperatorPending"
        | Insert -> "Insert"
        | Command -> "Command"
        | Language -> "Language"

[<RequireQualifiedAccess>]
type KeyMappingResult =

    /// The values were mappend completely and require no further mapping. This 
    /// could be a result of a no-op mapping though
    | Mapped of KeyInputSet

    /// The values were partially mapped but further mapping is required once the
    /// keys which were mapped are processed.  The values are 
    ///
    ///  mapped KeyInputSet * remaining KeyInputSet
    | PartiallyMapped of KeyInputSet * KeyInputSet

    /// The mapping encountered a recursive element that had to be broken 
    | Recursive

    /// More input is needed to resolve this mapping.
    | NeedsMoreInput of KeyInputSet

/// Flags for the substitute command
[<System.Flags>]
type SubstituteFlags = 
    | None = 0

    /// Replace all occurrences on the line
    | ReplaceAll = 0x1

    /// Ignore case for the search pattern
    | IgnoreCase = 0x2

    /// Report only 
    | ReportOnly = 0x4
    | Confirm = 0x8
    | UsePreviousFlags = 0x10
    | UsePreviousSearchPattern = 0x20
    | SuppressError = 0x40
    | OrdinalCase = 0x80
    | Magic = 0x100
    | Nomagic = 0x200

    /// The p option.  Print the last replaced line
    | PrintLast = 0x400

    /// The # option.  Print the last replaced line with the line number prepended
    | PrintLastWithNumber = 0x800

    /// Print the last line as if :list was used
    | PrintLastWithList = 0x1000

type SubstituteData = {
    SearchPattern : string
    Substitute : string
    Flags : SubstituteFlags
}

/// Represents the span for a Visual Character mode selection.  If it weren't for the
/// complications of tracking a visual character selection across edits to the buffer
/// there would really be no need for this and we could instead just represent it as 
/// a SnapshotSpan
[<StructuralEquality>]
[<NoComparison>]
[<Struct>]
[<DebuggerDisplay("{ToString()}")>]
type CharacterSpan
    (
        _start : SnapshotPoint,
        _lineCount : int,
        _lastLineLength : int
    ) =

    member x.Snapshot = _start.Snapshot

    member x.StartLine = SnapshotPointUtil.GetContainingLine x.Start

    member x.Start =  _start

    member x.LineCount = _lineCount

    member x.LastLineLength = _lastLineLength

    /// The last line in the CharacterSpan
    member x.LastLine = 
        let number = x.StartLine.LineNumber + (_lineCount - 1)
        SnapshotUtil.GetLineOrLast x.Snapshot number

    /// The last point included in the CharacterSpan
    member x.Last = 
        let endPoint : SnapshotPoint = x.End
        if endPoint.Position = x.Start.Position then
            None
        else
            SnapshotPoint(x.Snapshot, endPoint.Position - 1) |> Some

    /// Get the End point of the Character Span.
    member x.End =
        let snapshot = x.Snapshot
        let lastLine = x.LastLine
        let offset = 
            if _lineCount = 1 then
                // For a single line we need to apply the offset past the start point
                SnapshotPointUtil.GetColumn _start + _lastLineLength
            else
                _lastLineLength

        // The original SnapshotSpan could extend into the line break and hence we must
        // consider that here.  The most common case for this occuring is when the caret
        // in visual mode is on the first column of an empty line.  In that case the caret
        // is really in the line break so End is one past that
        let endPoint = SnapshotLineUtil.GetOffsetOrEndIncludingLineBreak offset lastLine

        // Make sure that we don't create a negative SnapshotSpan.  Really we should
        // be verifying the arguments to ensure we don't but until we do fix up
        // potential errors here
        if _start.Position <= endPoint.Position then
            endPoint
        else
            _start

    member x.Span = SnapshotSpan(x.Start, x.End)

    member x.Length = x.Span.Length

    override x.ToString() = x.Span.ToString()

    static member op_Equality(this,other) = System.Collections.Generic.EqualityComparer<CharacterSpan>.Default.Equals(this,other)
    static member op_Inequality(this,other) = not (System.Collections.Generic.EqualityComparer<CharacterSpan>.Default.Equals(this,other))

    static member CreateForSpan (span : SnapshotSpan) = 
        let lineCount = SnapshotSpanUtil.GetLineCount span
        let lastLine = SnapshotSpanUtil.GetLastLine span
        let lastLineLength = 
            if lineCount = 1 then
                span.End.Position - span.Start.Position
            else
                let diff = span.End.Position - lastLine.Start.Position
                max 0 diff
        CharacterSpan(span.Start, lineCount, lastLineLength)

/// Represents the span for a Visual Block mode selection
[<StructuralEquality>]
[<NoComparison>]
[<Struct>]
type BlockSpan
    (
        _start : SnapshotPoint,
        _width : int,
        _height : int
    ) = 

    member x.Start = _start

    /// In what column does this block span begin
    member x.Column = SnapshotPointUtil.GetColumn x.Start

    member x.Width = _width

    member x.Height = _height

    /// Get the EndPoint (exclusive) of the BlockSpan
    member x.End = 
        let line = 
            let lineNumber = SnapshotPointUtil.GetLineNumber x.Start
            SnapshotUtil.GetLineOrLast x.Snasphot (lineNumber + (_height - 1))
        let offset = x.Column + _width
        if offset >= line.Length then
            line.End
        else
            line.Start.Add offset

    member x.Snasphot = x.Start.Snapshot

    member x.TextBuffer =  x.Start.Snapshot.TextBuffer

    /// Get the NonEmptyCollection<SnapshotSpan> for the given block information
    member x.BlockSpans : NonEmptyCollection<SnapshotSpan> =
        let snapshot = SnapshotPointUtil.GetSnapshot x.Start
        let lineNumber = SnapshotPointUtil.GetLineNumber x.Start
        let list = System.Collections.Generic.List<SnapshotSpan>()
        for i = lineNumber to ((_height - 1) + lineNumber) do
            match SnapshotUtil.TryGetLine snapshot i with
            | None -> ()
            | Some line -> list.Add (SnapshotLineUtil.GetSpanInLine line x.Column _width)

        list
        |> NonEmptyCollectionUtil.OfSeq 
        |> Option.get

    override x.ToString() =
        sprintf "Point: %s Width: %d Height: %d" (x.Start.ToString()) _width _height

    static member op_Equality(this,other) = System.Collections.Generic.EqualityComparer<BlockSpan>.Default.Equals(this,other)
    static member op_Inequality(this,other) = not (System.Collections.Generic.EqualityComparer<BlockSpan>.Default.Equals(this,other))

    /// Create a BlockSpan for the given SnapshotSpan.  The returned BlockSpan will have a minumum of 1 for
    /// height and width.  The start of the BlockSpan is not necessarily the Start of the SnapshotSpan
    /// as an End column which occurs before the start could cause the BlockSpan start to be before the 
    /// SnapshotSpan start
    static member CreateForSpan (span : SnapshotSpan) = 
        let startPoint, width = 
            let startColumn = SnapshotPointUtil.GetColumn span.Start
            let endColumn = SnapshotPointUtil.GetColumn span.End
            let width = endColumn - startColumn

            if width = 0 then
                span.Start, 1
            elif width > 0 then
                span.Start, width
            else 
                let startLine = SnapshotPointUtil.GetContainingLine span.Start
                let start = SnapshotLineUtil.GetOffsetOrEnd endColumn startLine
                let width = abs width
                start, width

        let height = SnapshotSpanUtil.GetLineCount span
        BlockSpan(startPoint, width, height)

[<RequireQualifiedAccess>]
type BlockCaretLocation =
    | TopLeft
    | TopRight
    | BottomLeft
    | BottomRight

/// Represents a visual span of text in the form Vim understands.  This type understands 
/// nothing about the intricacies of Visual Mode selection.  It simply understands how
/// to represent the Spans it can occupy.
///
/// Note: There is no use of inclusive or exclusive in this type.  That is intentional.  This
/// type is simply a measurement.  The context in which it was measured is important for
/// the types which care about the context
[<RequireQualifiedAccess>]
[<StructuralEquality>]
[<NoComparison>]
[<DebuggerDisplay("{ToString()}")>]
type VisualSpan =

    /// A characterwise span.  The 'End' of the span is not selected.
    | Character of CharacterSpan

    /// A linewise span
    | Line of SnapshotLineRange

    /// A block span.  The first int in the number of lines and the second one is the 
    /// width of the selection
    | Block of BlockSpan

    with

    /// Return the Spans which make up this VisualSpan instance
    member x.Spans = 
        match x with 
        | VisualSpan.Character characterSpan -> [characterSpan.Span] |> Seq.ofList
        | VisualSpan.Line range -> [range.ExtentIncludingLineBreak] |> Seq.ofList
        | VisualSpan.Block blockSpan -> blockSpan.BlockSpans :> SnapshotSpan seq

    /// Returns the EditSpan for this VisualSpan
    member x.EditSpan = 
        match x with
        | VisualSpan.Character characterSpan -> EditSpan.Single characterSpan.Span
        | VisualSpan.Line range -> EditSpan.Single range.ExtentIncludingLineBreak
        | VisualSpan.Block blockSpan -> EditSpan.Block blockSpan.BlockSpans

    /// Returns the SnapshotLineRange for the VisualSpan.  For Character this will
    /// just expand out the Span.  For Line this is an identity.  For Block it will
    /// return the overarching span
    member x.LineRange = 
        match x with
        | VisualSpan.Character characterSpan -> SnapshotLineRangeUtil.CreateForSpan characterSpan.Span
        | VisualSpan.Line range -> range
        | VisualSpan.Block _ -> x.EditSpan.OverarchingSpan |> SnapshotLineRangeUtil.CreateForSpan

    /// Returns the start point of the Visual Span.  This can be None in the case
    /// of an empty Block selection.
    member x.Start =
        match x with
        | Character characterSpan -> characterSpan.Start
        | Line range ->  range.Start
        | Block blockSpan -> blockSpan.Start

    /// Get the end of the Visual Span
    member x.End = 
        match x with
        | VisualSpan.Character characterSpan -> characterSpan.End
        | VisualSpan.Line lineRange -> lineRange.End
        | VisualSpan.Block blockSpan -> blockSpan.End

    /// What type of OperationKind does this VisualSpan represent
    member x.OperationKind =
        match x with
        | VisualSpan.Character _ -> OperationKind.CharacterWise
        | VisualSpan.Line _ -> OperationKind.LineWise
        | VisualSpan.Block _ -> OperationKind.CharacterWise

    /// What type of ModeKind does this VisualSpan represent
    member x.ModeKind =
        match x with
        | VisualSpan.Character _ -> ModeKind.VisualCharacter
        | VisualSpan.Line _ -> ModeKind.VisualLine
        | VisualSpan.Block _ -> ModeKind.VisualBlock

    /// VisualKind of the VisualSpan
    member x.VisualKind = 
        match x with
        | VisualSpan.Character _ -> VisualKind.Character
        | VisualSpan.Block _ -> VisualKind.Block
        | VisualSpan.Line _ -> VisualKind.Line

    /// Select the given VisualSpan in the ITextView
    member x.Select (textView : ITextView) path =

        // Select the given SnapshotSpan
        let selectSpan startPoint endPoint = 

            textView.Selection.Mode <- TextSelectionMode.Stream

            let startPoint, endPoint = 
                match path with
                | Path.Forward -> startPoint, endPoint 
                | Path.Backward -> endPoint, startPoint

            // The editor will normalize SnapshotSpan values here which extend into the line break
            // portion of the line to not include the line break.  Must use VirtualSnapshotPoint 
            // values to ensure the proper selection
            let startPoint = startPoint |> VirtualSnapshotPointUtil.OfPointConsiderLineBreak
            let endPoint = endPoint |> VirtualSnapshotPointUtil.OfPointConsiderLineBreak

            textView.Selection.Select(startPoint, endPoint);

        match x with
        | Character characterSpan ->
            let endPoint = characterSpan.End
            selectSpan characterSpan.Start endPoint
        | Line lineRange ->
            selectSpan lineRange.Start lineRange.EndIncludingLineBreak
        | Block blockSpan ->
            textView.Selection.Mode <- TextSelectionMode.Box
            textView.Selection.Select(
                VirtualSnapshotPoint(blockSpan.Start),
                VirtualSnapshotPoint(blockSpan.End))

    override x.ToString() =
        match x with
        | VisualSpan.Character characterSpan -> sprintf "Character: %O" characterSpan
        | VisualSpan.Line lineRange -> sprintf "Line: %O" lineRange
        | VisualSpan.Block blockSpan -> sprintf "Block: %O" blockSpan

    /// Create the VisualSpan based on the specified points.  The activePoint is assumed
    /// to be the end of the selection and hence not included (exclusive) just as it is 
    /// in ITextSelection
    static member CreateForSelectionPoints visualKind (anchorPoint : SnapshotPoint) (activePoint : SnapshotPoint) =

        match visualKind with
        | VisualKind.Character ->
            let startPoint, endPoint = SnapshotPointUtil.OrderAscending anchorPoint activePoint
            SnapshotSpan(startPoint, endPoint) |> CharacterSpan.CreateForSpan |> Character
        | VisualKind.Line ->

            let startPoint, endPoint = SnapshotPointUtil.OrderAscending anchorPoint activePoint
            let startLine = SnapshotPointUtil.GetContainingLine startPoint

            // If endPoint is EndIncludingLineBreak we would get the line after and be 
            // one line too big.  Go back on point to ensure we don't expand the span
            let endLine = 
                if startPoint = endPoint then
                    startLine
                else
                    let endPoint = SnapshotPointUtil.SubtractOneOrCurrent endPoint
                    SnapshotPointUtil.GetContainingLine endPoint
            SnapshotLineRangeUtil.CreateForLineRange startLine endLine |> Line

        | VisualKind.Block -> 

            if activePoint = anchorPoint then
                // Special case of an empty selection.  The math below is predicated
                // on an non-empty selection.  Catch that case here
                BlockSpan(activePoint, 0, 1) |> Block
            else
                let anchorLine, anchorColumn = SnapshotPointUtil.GetLineColumn anchorPoint
                let activeLine, activeColumn = SnapshotPointUtil.GetLineColumn activePoint

                let width = activeColumn - anchorColumn |> abs
                let height = 
                    let height = anchorLine - activeLine
                    (abs height) + 1

                let startPoint =
                    let startLine = 
                        let number = min anchorLine activeLine
                        SnapshotUtil.GetLine anchorPoint.Snapshot number
                    let startColumn = min anchorColumn activeColumn
                    SnapshotLineUtil.GetOffsetOrEnd startColumn startLine

                BlockSpan(startPoint, width, height) |> Block

    /// Create a VisualSelection based off of the current selection.  If no selection is present
    /// then an empty VisualSpan will be created at the caret
    static member CreateForSelection (textView : ITextView) visualKind =
        let selection = textView.Selection
        if selection.IsEmpty then
            let caretPoint = TextViewUtil.GetCaretPoint textView
            VisualSpan.CreateForSelectionPoints visualKind caretPoint caretPoint
        else
            let anchorPoint = selection.AnchorPoint.Position
            let activePoint = selection.ActivePoint.Position 
            VisualSpan.CreateForSelectionPoints visualKind anchorPoint activePoint

    static member CreateForSpan (span : SnapshotSpan) visualKind =
        match visualKind with
        | VisualKind.Character -> span |> CharacterSpan.CreateForSpan |> Character
        | VisualKind.Line -> span |> SnapshotLineRangeUtil.CreateForSpan |> Line
        | VisualKind.Block -> span |> BlockSpan.CreateForSpan |> Block

/// Represents the information for a visual mode selection.  All of the values are expressed
/// in terms of an inclusive selection.
///
/// Note: It's intentional that inclusive and exclusive are not included in this particular
/// structure.  Whether or not the selection is inclusive or exclusive doesn't change the 
/// anchor / caret point.  It just changes what is operated on and what is actually 
/// physically selected by visual mode
[<RequireQualifiedAccess>] 
[<StructuralEquality>]
[<NoComparison>] 
type VisualSelection =

    /// The underlying span and whether or not this is a forward looking span
    | Character of CharacterSpan * Path

    /// The underlynig range, whether or not is forwards or backwards and the int 
    /// is which column in the range the caret should be placed in
    | Line of SnapshotLineRange * Path * int 

    /// Just keep the BlockSpan and the caret information for the block
    | Block of BlockSpan * BlockCaretLocation

    with

    member x.IsCharacterForward =
        match x with
        | Character (_, path) -> path.IsPathForward
        | _ -> false

    member x.IsLineForward = 
        match x with
        | Line (_, path, _) -> path.IsPathForward
        | _ -> false

    /// Get the ModeKind for the VisualSelection
    member x.ModeKind = 
        match x with
        | Character _ -> ModeKind.VisualCharacter
        | Line _ -> ModeKind.VisualLine
        | Block _ -> ModeKind.VisualBlock

    /// The underlying VisualSpan
    member x.VisualSpan =
        match x with 
        | Character (characterSpan, _) -> VisualSpan.Character characterSpan
        | Line (lineRange, _, _) -> VisualSpan.Line lineRange
        | Block (blockSpan, _) -> VisualSpan.Block blockSpan

    /// Get the VisualSelection information adjusted for the given selection kind.  This is only useful 
    /// when a VisualSelection is created from the caret position and the selection needs to be adjusted
    /// to include or exclude the caret.  
    /// 
    /// It's incorrect to use when creating from an actual physical selection
    member x.AdjustForSelectionKind selectionKind = 
        match selectionKind with
        | SelectionKind.Inclusive -> x
        | SelectionKind.Exclusive -> 
            match x with
            | Character (characterSpan, path) -> 
                // The span decreases by a single character in exclusive 
                let endPoint = characterSpan.Last |> OptionUtil.getOrDefault characterSpan.Start
                let characterSpan = SnapshotSpan(characterSpan.Start, endPoint) |> CharacterSpan.CreateForSpan
                VisualSelection.Character (characterSpan, path)
            | Line _ ->
                // The span isn't effected
                x
            | Block (blockSpan, blockCaretLocation) -> 
                // The width of a block span decreases by 1 in exclusive.  The minimum though
                // is still 1
                let width = max (blockSpan.Width - 1) 1
                let blockSpan = BlockSpan(blockSpan.Start, width, blockSpan.Height)
                VisualSelection.Block (blockSpan, blockCaretLocation)

    /// Gets the SnapshotPoint for the caret as it should appear in the given VisualSelection with the 
    /// specified SelectionKind.  
    member x.GetCaretPoint selectionKind = 

        let getAdjustedEnd (span : SnapshotSpan) = 
            match selectionKind with
            | SelectionKind.Exclusive -> span.End
            | SelectionKind.Inclusive -> 
                if span.Length > 0 then
                    SnapshotPointUtil.SubtractOne span.End
                else
                    span.End

        match x with
        | Character (characterSpan, path) ->
            // The caret is either positioned at the start or the end of the selected
            // SnapshotSpan
            if path.IsPathForward && characterSpan.Length > 0 then
                getAdjustedEnd characterSpan.Span
            else
                characterSpan.Start

        | Line (snapshotLineRange, path, column) ->

            // The caret is either positioned at the start or the end of the selected range
            // and can be on any column in either
            let line = 
                if path.IsPathForward then
                    snapshotLineRange.LastLine
                else
                    snapshotLineRange.StartLine

            if column <= line.LengthIncludingLineBreak then
                SnapshotPointUtil.Add column line.Start
            else
                line.End

        | Block (blockSpan, blockCaretLocation) ->

            match blockCaretLocation with
            | BlockCaretLocation.TopLeft -> blockSpan.Start
            | BlockCaretLocation.TopRight -> getAdjustedEnd blockSpan.BlockSpans.Head
            | BlockCaretLocation.BottomLeft -> blockSpan.BlockSpans |> SeqUtil.last |> SnapshotSpanUtil.GetStartPoint
            | BlockCaretLocation.BottomRight -> blockSpan.BlockSpans |> SeqUtil.last |> getAdjustedEnd


    /// Select the given VisualSpan in the ITextView
    member x.Select (textView : ITextView) =
        let path = 
            match x with
            | Character (_, path) -> path
            | Line (_, path, _) -> path
            | Block _ -> Path.Forward
        x.VisualSpan.Select textView path

    /// Create for the given VisualSpan.  Assumes this was a forward created VisualSpan
    static member CreateForward visualSpan = 
        match visualSpan with
        | VisualSpan.Character span -> 
            VisualSelection.Character (span, Path.Forward)
        | VisualSpan.Line lineRange ->
            let column = SnapshotPointUtil.GetColumn lineRange.LastLine.End
            VisualSelection.Line (lineRange, Path.Forward, column)
        | VisualSpan.Block blockSpan ->
            VisualSelection.Block (blockSpan, BlockCaretLocation.BottomRight)

    /// Create the VisualSelection over the VisualSpan with the specified caret location
    static member Create (visualSpan : VisualSpan) path (caretPoint : SnapshotPoint) =
        match visualSpan with
        | VisualSpan.Character characterSpan ->
            Character (characterSpan, path)
        | VisualSpan.Line lineRange ->
            let column = SnapshotPointUtil.GetColumn caretPoint
            Line (lineRange, path, column)

        | VisualSpan.Block blockSpan ->

            // Need to calculate the caret location.  Do this based on the initial anchor and
            // caret locations
            let blockCaretLocation = 
                let startLine, startColumn = SnapshotPointUtil.GetLineColumn blockSpan.Start
                let caretLine, caretColumn = SnapshotPointUtil.GetLineColumn caretPoint
                match caretLine > startLine, caretColumn > startColumn with
                | true, true -> BlockCaretLocation.BottomRight
                | true, false -> BlockCaretLocation.BottomLeft
                | false, true -> BlockCaretLocation.TopRight
                | false, false -> BlockCaretLocation.TopLeft

            Block (blockSpan, blockCaretLocation)

    /// Create a VisualSelection for the given anchor point and caret.  The caret is assumed to 
    /// be the last point included in the selection (inclusive)
    static member CreateForPoints visualKind (anchorPoint : SnapshotPoint) (caretPoint : SnapshotPoint) =

        let createBlock () =
            let anchorColumn = SnapshotPointUtil.GetColumn anchorPoint
            let caretColumn = SnapshotPointUtil.GetColumn caretPoint
            if anchorColumn <= caretColumn then
                // It's a forward block selection.  The active point needs to be one column past the 
                // caret in order to ensure the caret is selected 
                let activePoint = SnapshotPointUtil.AddOneOrCurrent caretPoint
                VisualSpan.CreateForSelectionPoints VisualKind.Block anchorPoint activePoint, Path.Forward
            else
                // It's a backward selection.  The caretPoint is in a lesser column and hence will
                // be included.  Need to adjust the anchor point though by one to put it in the 
                // selection
                let anchorPoint = SnapshotPointUtil.AddOneOrCurrent anchorPoint
                VisualSpan.CreateForSelectionPoints VisualKind.Block anchorPoint caretPoint, Path.Backward

        let createNormal () = 

            let isForward = anchorPoint.Position <= caretPoint.Position
            let anchorPoint, activePoint = 
                if isForward then
                    let activePoint = SnapshotPointUtil.AddOneOrCurrent caretPoint
                    anchorPoint, activePoint
                else
                    let activePoint = SnapshotPointUtil.AddOneOrCurrent anchorPoint
                    caretPoint, activePoint

            let path = Path.Create isForward
            VisualSpan.CreateForSelectionPoints visualKind anchorPoint activePoint, path

        let visualSpan, path = 
            match visualKind with
            | VisualKind.Block -> createBlock ()
            | VisualKind.Line -> createNormal ()
            | VisualKind.Character -> createNormal ()

        VisualSelection.Create visualSpan path caretPoint

    /// Create a VisualSelection based off of the current selection and position of the caret.  The
    /// SelectionKind should specify what the current mode is (or the mode which produced the 
    /// active ITextSelection)
    static member CreateForSelection (textView : ITextView) visualKind selectionKind =
        let caretPoint = TextViewUtil.GetCaretPoint textView
        let visualSpan = VisualSpan.CreateForSelection textView visualKind

        // Get the proper VisualSpan based off of the way in which it was created.  VisualSelection
        // represents all values internally as inclusive
        let visualSpan = 
            match selectionKind with
            | SelectionKind.Inclusive -> visualSpan
            | SelectionKind.Exclusive ->
                match visualSpan with
                | VisualSpan.Character characterSpan ->
                    let endPoint = SnapshotPointUtil.AddOneOrCurrent characterSpan.End
                    SnapshotSpan(characterSpan.Start, endPoint) |> CharacterSpan.CreateForSpan |> VisualSpan.Character
                | VisualSpan.Line _ -> visualSpan
                | VisualSpan.Block blockSpan ->
                    let width = blockSpan.Width + 1
                    let blockSpan = BlockSpan(blockSpan.Start, width, blockSpan.Height)
                    VisualSpan.Block blockSpan

        let path = 
            if textView.Selection.IsReversed then
                Path.Backward
            else
                Path.Forward

        let caretPoint = TextViewUtil.GetCaretPoint textView
        VisualSelection.Create visualSpan path caretPoint

    /// Create the initial Visual Selection information for the specified Kind started at 
    /// the specified point
    static member CreateInitial visualKind caretPoint =
        match visualKind with
        | VisualKind.Character ->
            let characterSpan = 
                let endPoint = SnapshotPointUtil.AddOneOrCurrent caretPoint
                let span = SnapshotSpan(caretPoint, endPoint)
                CharacterSpan.CreateForSpan span
            VisualSelection.Character (characterSpan, Path.Forward)
        | VisualKind.Line ->
            let lineRange = 
                let line = SnapshotPointUtil.GetContainingLine caretPoint
                SnapshotLineRangeUtil.CreateForLine line
            let column = SnapshotPointUtil.GetColumn caretPoint
            VisualSelection.Line (lineRange, Path.Forward, column)
        | VisualKind.Block ->
            let blockSpan = BlockSpan(caretPoint, 1, 1)
            VisualSelection.Block (blockSpan, BlockCaretLocation.BottomRight)

/// Most text object entries have specific effects on Visual Mode.  They are 
/// described below
[<RequireQualifiedAccess>]
type TextObjectKind = 
    | None
    | LineToCharacter
    | AlwaysCharacter
    | AlwaysLine

[<RequireQualifiedAccess>]
type ModeArgument =
    | None

    /// Used for transitions from Visual Mode directly to Command mode
    | FromVisual 

    /// Passed to visual mode to indicate what the initial selection should be.  The SnapshotPoint
    /// option provided is meant to be the initial caret point.  If not provided the actual 
    /// caret point is used
    | InitialVisualSelection of VisualSelection * SnapshotPoint option

    /// Begins a block insertion.  This can possibly have a linked undo transaction that needs
    /// to be carried forward through the insert
    | InsertBlock of BlockSpan * ILinkedUndoTransaction

    /// Begins insert mode with a specified count.  This means the text inserted should
    /// be repeated a total of 'count - 1' times when insert mode exits
    | InsertWithCount of int

    /// Begins insert mode with a specified count.  This means the text inserted should
    /// be repeated a total of 'count - 1' times when insert mode exits.  Each extra time
    /// should be on a new line
    | InsertWithCountAndNewLine of int

    /// Begins insert mode with an existing UndoTransaction.  This is used to link 
    /// change commands with text changes.  For example C, c, etc ...
    | InsertWithTransaction of ILinkedUndoTransaction

    /// Passing the substitute to confirm to Confirm mode.  The SnapshotSpan is the first
    /// match to process and the range is the full range to consider for a replace
    | Substitute of SnapshotSpan * SnapshotLineRange * SubstituteData

type ModeSwitch =
    | NoSwitch
    | SwitchMode of ModeKind
    | SwitchModeWithArgument of ModeKind * ModeArgument
    | SwitchPreviousMode 

    /// Switch to the given mode for a single command.  After the command is processed switch
    /// back to the original mode
    | SwitchModeOneTimeCommand

// TODO: Should be succeeded or something other than Completed.  Error also completed just not
// well
[<RequireQualifiedAccess>]
type CommandResult =   

    /// The command completed and requested a switch to the provided Mode which 
    /// may just be a no-op
    | Completed  of ModeSwitch

    /// An error was encountered and the command was unable to run.  If this is encountered
    /// during a macro run it will cause the macro to stop executing
    | Error

[<RequireQualifiedAccess>]
type RunResult = 
    | Completed
    | SubstituteConfirm of SnapshotSpan * SnapshotLineRange * SubstituteData

/// Information about the attributes of Command
[<System.Flags>]
type CommandFlags =
    | None = 0x0

    /// Relates to the movement of the cursor.  A movement command does not alter the 
    /// last command
    | Movement = 0x1

    /// A Command which can be repeated
    | Repeatable = 0x2

    /// A Command which should not be considered when looking at last changes
    | Special = 0x4

    /// Can handle the escape key if provided as part of a Motion or Long command extra
    /// input
    | HandlesEscape = 0x8

    /// For the purposes of change repeating the command is linked with the following
    /// text change
    | LinkedWithNextCommand = 0x10

    /// For the purposes of change repeating the command is linked with the previous
    /// text change if it exists
    | LinkedWithPreviousCommand = 0x20

    /// For Visual Mode commands which should reset the cursor to the original point
    /// after completing
    | ResetCaret = 0x40

    /// Vim allows for special handling of the 'd' command in normal mode such that it can
    /// have the pattern 'd#d'.  This flag is used to tag the 'd' command to allow such
    /// a pattern
    | Delete = 0x80

    /// Vim allows for special handling of the 'y' command in normal mode such that it can
    /// have the pattern 'y#y'.  This flag is used to tag the 'd' command to allow such
    /// a pattern
    | Yank = 0x100

    /// Represents an insert edit action which can be linked with other insert edit actions and
    /// hence acts with them in a repeat
    | InsertEdit = 0x200

/// Data about the run of a given MotionResult
type MotionData = {

    /// The associated Motion value
    Motion : Motion

    /// The argument which should be supplied to the given Motion
    MotionArgument : MotionArgument
}

/// Data needed to execute a command
type CommandData = {

    /// The raw count provided to the command
    Count : int option 

    /// The register name specified for the command 
    RegisterName : RegisterName option

} with

    /// Return the provided count or the default value of 1
    member x.CountOrDefault = 
        match x.Count with 
        | Some count -> count
        | None -> 1

/// We want the NormalCommand discriminated union to have structural equality in order
/// to ease testing requirements.  In order to do this and support Ping we need a 
/// separate type here to wrap the Func to be comparable.  Does so in a reference 
/// fashion
type PingData (_func : CommandData -> CommandResult) = 

    member x.Function = _func

    static member op_Equality (this, other) = System.Object.ReferenceEquals(this, other)
    static member op_Inequality (this, other) = not (System.Object.ReferenceEquals(this, other))
    override x.GetHashCode() = 1
    override x.Equals(obj) = System.Object.ReferenceEquals(x, obj)
    interface System.IEquatable<PingData> with
        member x.Equals other = x.Equals(other)

/// Normal mode commands which can be executed by the user
[<RequireQualifiedAccess>]
[<StructuralEquality>]
[<NoComparison>]
type NormalCommand = 

    /// Add 'count' to the word close to the caret
    | AddToWord

    /// Deletes the text specified by the motion and begins insert mode. Implements the "c" 
    /// command
    | ChangeMotion of MotionData

    /// Change the characters on the caret line 
    | ChangeCaseCaretLine of ChangeCharacterKind

    /// Change the characters on the caret line 
    | ChangeCaseCaretPoint of ChangeCharacterKind

    /// Change case of the specified motion
    | ChangeCaseMotion of ChangeCharacterKind * MotionData

    /// Delete 'count' lines and begin insert mode
    | ChangeLines

    /// Delete the text till the end of the line in the same manner as DeleteTillEndOfLine
    /// and start Insert Mode
    | ChangeTillEndOfLine

    /// Close all folds in the buffer
    | CloseAllFolds

    /// Close all folds under the caret
    | CloseAllFoldsUnderCaret

    /// Close the IVimBuffer and don't bother to save
    | CloseBuffer

    /// Close 'count' folds under the caret
    | CloseFoldUnderCaret

    /// Delete all of the folds that are in the ITextBuffer
    | DeleteAllFoldsInBuffer

    /// Delete the character at the current cursor position.  Implements the "x" command
    | DeleteCharacterAtCaret

    /// Delete the character before the cursor. Implements the "X" command
    | DeleteCharacterBeforeCaret

    /// Delete the fold under the caret
    | DeleteFoldUnderCaret

    /// Delete all folds under the caret
    | DeleteAllFoldsUnderCaret

    /// Delete lines from the buffer: dd
    | DeleteLines

    /// Delete the specified motion of text
    | DeleteMotion of MotionData

    /// Delete till the end of the line and 'count - 1' more lines down
    | DeleteTillEndOfLine

    /// Fold 'count' lines in the ITextBuffer
    | FoldLines

    /// Create a fold over the specified motion 
    | FoldMotion of MotionData

    /// Format the specified lines
    | FormatLines

    /// Format the specified motion
    | FormatMotion of MotionData

    /// Go to the definition of hte word under the caret.
    | GoToDefinition

    /// GoTo the file under the cursor.  The bool represents whether or not this should occur in
    /// a different window
    | GoToFileUnderCaret of bool

    /// Go to the global declaration of the word under the caret
    | GoToGlobalDeclaration

    /// Go to the local declaration of the word under the caret
    | GoToLocalDeclaration

    /// Go to the next tab in the specified direction
    | GoToNextTab of Path

    /// GoTo the ITextView in the specified direction
    | GoToView of Direction

    /// Switch to insert after the caret position
    | InsertAfterCaret

    /// Switch to insert mode
    | InsertBeforeCaret

    /// Switch to insert mode at the end of the line
    | InsertAtEndOfLine

    /// Insert text at the first non-blank line in the current line
    | InsertAtFirstNonBlank

    /// Insert text at the start of a line (column 0)
    | InsertAtStartOfLine

    /// Insert a line above the cursor and begin insert mode
    | InsertLineAbove

    /// Insert a line below the cursor and begin insert mode
    | InsertLineBelow

    /// Join the specified lines
    | JoinLines of JoinKind

    /// Jump to the specified mark 
    | JumpToMark of Mark

    /// Jump to the start of the line for the specified mark
    | JumpToMarkLine of Mark

    /// Jump to the next older item in the tag list
    | JumpToOlderPosition

    /// Jump to the next new item in the tag list
    | JumpToNewerPosition

    /// Move the caret to the result of the given Motion.
    | MoveCaretToMotion of Motion

    /// Undo count operations in the ITextBuffer
    | Undo

    /// Open all folds in the buffer
    | OpenAllFolds

    /// Open all of the folds under the caret
    | OpenAllFoldsUnderCaret

    /// Open a fold under the caret
    | OpenFoldUnderCaret

    /// Toggle a fold under the caret
    | ToggleFoldUnderCaret

    /// Toggle all folds under the caret
    | ToggleAllFolds

    /// Not actually a Vim Command.  This is a simple ping command which makes 
    /// testing items like complex repeats significantly easier
    | Ping of PingData

    /// Put the contents of the register into the buffer after the cursor.  The bool is 
    /// whether or not the caret should be placed after the inserted text
    | PutAfterCaret of bool

    /// Put the contents of the register into the buffer after the cursor and respecting 
    /// the indent of the current line
    | PutAfterCaretWithIndent

    /// Put the contents of the register into the buffer before the cursor.  The bool is 
    /// whether or not the caret should be placed after the inserted text
    | PutBeforeCaret of bool

    /// Put the contents of the register into the buffer before the cursor and respecting 
    /// the indent of the current line
    | PutBeforeCaretWithIndent

    /// Start the recording of a macro to the specified Register
    | RecordMacroStart of char

    /// Stop the recording of a macro to the specified Register
    | RecordMacroStop

    /// Redo count operations in the ITextBuffer
    | Redo

    /// Repeat the last command
    | RepeatLastCommand

    /// Repeat the last substitute command.  The bool value is for whether or not the flags
    /// from the last substitute should be reused as well
    | RepeatLastSubstitute of bool

    /// Replace the text starting at the text by starting insert mode
    | ReplaceAtCaret

    /// Replace the char under the cursor with the given char
    | ReplaceChar of KeyInput

    /// Run the macro contained in the register specified by the char value
    | RunMacro of char

    /// Set the specified mark to the current value of the caret
    | SetMarkToCaret of char

    /// Scroll the screen in the specified direction.  The bool is whether to use
    /// the 'scroll' option or 'count'
    | ScrollLines of ScrollDirection * bool

    /// Move the display a single page in the specified direction
    | ScrollPages of ScrollDirection

    /// Scroll the current line to the top of the ITextView.  The bool is whether or not
    /// to leave the caret in the same column
    | ScrollCaretLineToTop of bool

    /// Scroll the caret line to the middle of the ITextView.  The bool is whether or not
    /// to leave the caret in the same column
    | ScrollCaretLineToMiddle of bool

    /// Scroll the caret line to the bottom of the ITextView.  The bool is whether or not
    /// to leave the caret in the same column
    | ScrollCaretLineToBottom of bool

    /// Shift 'count' lines from the cursor left
    | ShiftLinesLeft

    /// Shift 'count' lines from the cursor right
    | ShiftLinesRight

    /// Shift 'motion' lines from the cursor left
    | ShiftMotionLinesLeft of MotionData

    /// Shift 'motion' lines from the cursor right
    | ShiftMotionLinesRight of MotionData

    /// Split the view horizontally
    | SplitViewHorizontally

    /// Split the view vertically
    | SplitViewVertically

    /// Substitute the character at the cursor
    | SubstituteCharacterAtCaret

    /// Subtract 'count' from the word at the caret
    | SubtractFromWord

    /// Switch modes with the specified information
    | SwitchMode of ModeKind * ModeArgument

    /// Switch to the previous Visual Mode selection
    | SwitchPreviousVisualMode

    /// Write out the ITextBuffer and quit
    | WriteBufferAndQuit

    /// Yank the given motion into a register
    | Yank of MotionData

    /// Yank the specified number of lines
    | YankLines

/// Visual mode commands which can be executed by the user 
[<RequireQualifiedAccess>]
type VisualCommand = 

    /// Change the case of the selected text in the specified manner
    | ChangeCase of ChangeCharacterKind

    /// Delete the selection and begin insert mode.  Implements the 'c' and 's' commands
    | ChangeSelection

    /// Delete the selected lines and begin insert mode ('S' and 'C' commands).  The bool parameter
    /// is whether or not to treat block selection as a special case
    | ChangeLineSelection of bool

    /// Close a fold in the selection
    | CloseFoldInSelection

    /// Close all folds in the selection
    | CloseAllFoldsInSelection

    /// Delete a fold in the selection
    | DeleteFoldInSelection

    /// Delete all folds in the selection
    | DeleteAllFoldsInSelection

    /// Delete the selected lines
    | DeleteLineSelection

    /// Delete the selected text and put it into a register
    | DeleteSelection

    /// Fold the current selected lines
    | FoldSelection

    /// Format the selected text
    | FormatLines

    /// Join the selected lines
    | JoinSelection of JoinKind

    /// Move the caret to the result of the given Motion.  This movement is from a 
    /// text-object selection.  Certain motions 
    | MoveCaretToTextObject of Motion * TextObjectKind

    /// Open all folds in the selection
    | OpenAllFoldsInSelection

    /// Open one fold in the selection
    | OpenFoldInSelection

    /// Put the contents af the register after the selection.  The bool is for whether or not the
    // caret should be placed after the inserted text
    | PutOverSelection of bool

    /// Replace the visual span with the provided character
    | ReplaceSelection of KeyInput

    /// Shift the selected lines left
    | ShiftLinesLeft

    /// Shift the selected lines to the right
    | ShiftLinesRight

    /// Switch the mode to insert and possibly a block insert
    | SwitchModeInsert

    /// Switch to the specified visual mode
    | SwitchModeVisual of VisualKind

    /// Toggle one fold in the selection
    | ToggleFoldInSelection

    /// Toggle all folds in the selection
    | ToggleAllFoldsInSelection

    /// Yank the lines which are specified by the selection
    | YankLineSelection

    /// Yank the selection into the specified register
    | YankSelection

/// Insert mode commands that can be executed by the user
[<RequireQualifiedAccess>]
[<StructuralEquality>]
[<NoComparison>]
type InsertCommand  =

    /// Backspace at the current caret position
    | Back

    /// This is an insert command which is a combination of other insert commands
    | Combined of InsertCommand * InsertCommand

    /// Complete the Insert Mode session.  This is done as a command so that it will 
    /// be a bookend of insert mode for the repeat infrastructure
    ///
    /// The bool value represents whether or not the caret needs to be moved to the
    /// left
    | CompleteMode of bool

    /// Delete the character under the caret
    | Delete

    /// Delete all indentation on the current line
    | DeleteAllIndent

    /// Delete the word before the cursor
    | DeleteWordBeforeCursor

    /// Direct insert of the specified char
    | DirectInsert of char

    /// Direct replacement of the spceified char
    | DirectReplace of char

    /// Insert a new line into the ITextBuffer
    | InsertNewLine

    /// Insert a tab into the ITextBuffer
    | InsertTab

    /// Insert the specified text into the ITextBuffer
    | InsertText of string

    /// Move the caret in the given direction
    | MoveCaret of Direction

    /// Move the caret in the given direction by a whole word
    | MoveCaretByWord of Direction

    /// Shift the current line one indent width to the left
    | ShiftLineLeft 

    /// Shift the current line one indent width to the right
    | ShiftLineRight

    /// This covers edits which weren't directly typed by the user.  It covers
    /// a range of scenarios including
    ///  - Repeated portion of count / block edits
    ///  - Edits which occur outside of Vim
    | ExtraTextChange of TextChange

    with

    /// For insert only commands this will hold the insert text
    member x.GetInsertText editorOptions = 
        let rec inner command = 
            match command with
            | Back -> None
            | Combined (left, right) ->
                match inner left, inner right with
                | Some left, Some right -> left + right |> Some
                | _ -> None
            | CompleteMode _ -> None
            | Delete -> None
            | DeleteAllIndent -> None
            | DeleteWordBeforeCursor -> None
            | DirectInsert c -> Some (c.ToString())
            | DirectReplace c -> Some (c.ToString())
            | InsertNewLine -> EditUtil.NewLine editorOptions |> Some
            | InsertTab -> Some "\t"
            | InsertText text -> Some text
            | MoveCaret _ -> None
            | MoveCaretByWord _ -> None
            | ShiftLineLeft -> None
            | ShiftLineRight -> None
            | ExtraTextChange textChange -> textChange.InsertText 

        inner x

/// Commands which can be executed by the user
[<RequireQualifiedAccess>]
[<StructuralEquality>]
[<NoComparison>]
type Command =

    /// A Normal Mode Command
    | NormalCommand of NormalCommand * CommandData

    /// A Visual Mode Command
    | VisualCommand of VisualCommand * CommandData * VisualSpan

    /// An Insert / Replace Mode Command
    | InsertCommand of InsertCommand

/// The result of binding to a Motion value.
[<RequireQualifiedAccess>]
type BindResult<'T> = 

    /// Successfully bound to a value
    | Complete of 'T 

    /// More input is needed to complete the binding operation
    | NeedMoreInput of BindData<'T>

    /// There was an error completing the binding operation
    | Error

    /// Motion was cancelled via user input
    | Cancelled

    with

    static member CreateNeedMoreInput keyRemapModeOpt bindFunc =
        let data = { KeyRemapMode = keyRemapModeOpt; BindFunction = bindFunc }
        NeedMoreInput data

    /// Used to compose to BindResult<'T> functions together by forwarding from
    /// one to the other once the value is completed
    member x.Map mapFunc =
        match x with
        | Complete value -> mapFunc value
        | NeedMoreInput bindData -> NeedMoreInput (bindData.Map mapFunc)
        | Error -> Error
        | Cancelled -> Cancelled

    /// Used to convert a BindResult<'T>.Completed to BindResult<'U>.Completed through a conversion
    /// function
    member x.Convert convertFunc = 
        x.Map (fun value -> convertFunc value |> BindResult.Complete)

and BindData<'T> = {

    /// The optional KeyRemapMode which should be used when binding
    /// the next KeyInput in the sequence
    KeyRemapMode : KeyRemapMode option

    /// Function to call to get the BindResult for this data
    BindFunction : KeyInput -> BindResult<'T>

} with

    /// Many bindings are simply to get a single KeyInput.  Centralize that logic 
    /// here so it doesn't need to be repeated
    static member CreateForSingle keyRemapModeOpt completeFunc =
        let inner keyInput =
            if keyInput = KeyInputUtil.EscapeKey then
                BindResult.Cancelled
            else
                let data = completeFunc keyInput
                BindResult<'T>.Complete data
        { KeyRemapMode = keyRemapModeOpt; BindFunction = inner }

    /// Many bindings are simply to get a single char.  Centralize that logic 
    /// here so it doesn't need to be repeated
    static member CreateForSingleChar keyRemapModeOpt completeFunc = 
        BindData<_>.CreateForSingle keyRemapModeOpt (fun keyInput -> completeFunc keyInput.Char)

    /// Create for a function which doesn't require any remapping
    static member CreateForSimple bindFunc =
        { KeyRemapMode = None; BindFunction = bindFunc }

    /// Often types bindings need to compose together because we need an inner binding
    /// to succeed so we can create a projected value.  This function will allow us
    /// to translate a BindData<'T>.Completed -> BindData<'U>.Completed
    member x.Convert convertFunc = 
        x.Map (fun value -> convertFunc value |> BindResult.Complete)

    /// Very similar to the Convert function.  This will instead map a BindData<'T>.Completed
    /// to a BindData<'U> of any form 
    member x.Map mapFunc = 

        let rec inner bindFunction keyInput = 
            match x.BindFunction keyInput with
            | BindResult.Cancelled -> BindResult.Cancelled
            | BindResult.Complete value -> mapFunc value
            | BindResult.Error -> BindResult.Error
            | BindResult.NeedMoreInput bindData -> BindResult.NeedMoreInput (bindData.Map mapFunc)

        { KeyRemapMode = x.KeyRemapMode; BindFunction = inner x.BindFunction }

/// Several types of BindData<'T> need to take an action when a binding begins against
/// themselves.  This action needs to occur before the first KeyInput value is processed
/// and hence they need a jump start.  The most notable is IncrementalSearch which 
/// needs to enter 'Search' mode before processing KeyInput values so the cursor can
/// be updated
[<RequireQualifiedAccess>]
type BindDataStorage<'T> =

    /// Simple BindData<'T> which doesn't require activation
    | Simple of BindData<'T> 

    /// Complex BindData<'T> which does require activation
    | Complex of (unit -> BindData<'T>)

    with

    /// Creates the BindData
    member x.CreateBindData () = 
        match x with
        | Simple bindData -> bindData
        | Complex func -> func()

    /// Convert from a BindDataStorage<'T> -> BindDataStorage<'U>.  The 'mapFunc' value
    /// will run on the final 'T' data if it eventually is completed
    member x.Convert mapFunc = 
        match x with
        | Simple bindData -> Simple (bindData.Convert mapFunc)
        | Complex func -> Complex (fun () -> func().Convert mapFunc)

    /// Many bindings are simply to get a single char.  Centralize that logic 
    /// here so it doesn't need to be repeated
    static member CreateForSingleChar keyRemapModeOpt completeFunc = 
        let data = BindData<_>.CreateForSingle keyRemapModeOpt (fun keyInput -> completeFunc keyInput.Char)
        BindDataStorage<_>.Simple data

/// Representation of binding of Command's to KeyInputSet values and flags which correspond
/// to the execution of the command
[<DebuggerDisplay("{ToString(),nq}")>]
[<RequireQualifiedAccess>]
type CommandBinding = 

    /// KeyInputSet bound to a particular NormalCommand instance
    | NormalBinding of KeyInputSet * CommandFlags * NormalCommand

    /// KeyInputSet bound to a complex NormalCommand instance
    | ComplexNormalBinding of KeyInputSet * CommandFlags * BindDataStorage<NormalCommand>

    /// KeyInputSet bound to a particular NormalCommand instance which takes a Motion Argument
    | MotionBinding of KeyInputSet * CommandFlags * (MotionData -> NormalCommand)

    /// KeyInputSet bound to a particular VisualCommand instance
    | VisualBinding of KeyInputSet * CommandFlags * VisualCommand

    /// KeyInputSet bound to an insert mode command
    | InsertBinding of KeyInputSet * CommandFlags * InsertCommand

    /// KeyInputSet bound to a complex VisualCommand instance
    | ComplexVisualBinding of KeyInputSet * CommandFlags * BindDataStorage<VisualCommand>

    with 

    /// The raw command inputs
    member x.KeyInputSet = 
        match x with
        | NormalBinding (value, _, _) -> value
        | MotionBinding (value, _, _) -> value
        | VisualBinding (value, _, _) -> value
        | InsertBinding (value, _, _) -> value
        | ComplexNormalBinding (value, _, _) -> value
        | ComplexVisualBinding (value, _, _) -> value

    /// The kind of the Command
    member x.CommandFlags =
        match x with
        | NormalBinding (_, value, _) -> value
        | MotionBinding (_, value, _) -> value
        | VisualBinding (_, value, _) -> value
        | InsertBinding (_, value, _) -> value
        | ComplexNormalBinding (_, value, _) -> value
        | ComplexVisualBinding (_, value, _) -> value

    /// Is the Repeatable flag set
    member x.IsRepeatable = Util.IsFlagSet x.CommandFlags CommandFlags.Repeatable

    /// Is the HandlesEscape flag set
    member x.HandlesEscape = Util.IsFlagSet x.CommandFlags CommandFlags.HandlesEscape

    /// Is the Movement flag set
    member x.IsMovement = Util.IsFlagSet x.CommandFlags CommandFlags.Movement

    /// Is the Special flag set
    member x.IsSpecial = Util.IsFlagSet x.CommandFlags CommandFlags.Special

    override x.ToString() = System.String.Format("{0} -> {1}", x.KeyInputSet, x.CommandFlags)

/// Used to execute commands
and ICommandUtil = 

    /// Run a normal command
    abstract RunNormalCommand : command : NormalCommand -> commandData : CommandData -> CommandResult

    /// Run a visual command
    abstract RunVisualCommand : command : VisualCommand -> commandData: CommandData -> visualSpan : VisualSpan -> CommandResult

    /// Run a insert command
    abstract RunInsertCommand : command : InsertCommand -> CommandResult

    /// Run a command
    abstract RunCommand : command : Command -> CommandResult

type internal IInsertUtil = 

    /// Run a insert command
    abstract RunInsertCommand : InsertCommand -> CommandResult

    /// Repeat the given edit series. 
    abstract RepeatEdit : textChange : TextChange -> addNewLines : bool -> count : int -> unit

    /// Repeat the given edit series. 
    abstract RepeatBlock : InsertCommand -> blockSpan : BlockSpan -> unit

/// Contains the stored information about a Visual Span.  This instance *will* be 
/// stored for long periods of time and used to repeat a Command instance across
/// multiple IVimBuffer instances so it must be buffer agnostic
[<RequireQualifiedAccess>]
type StoredVisualSpan = 

    /// Storing a character wise span.  Need to know the line count and the offset 
    /// in the last line for the end.  
    | Character of int * int

    /// Storing a linewise span just stores the count of lines
    | Line of int

    /// Storing of a block span records the length of the span and the number of
    /// lines which should be affected by the Span
    | Block of int * int

    with

    /// Create a StoredVisualSpan from the provided VisualSpan value
    static member OfVisualSpan visualSpan = 
        match visualSpan with
        | VisualSpan.Character characterSpan ->
            StoredVisualSpan.Character (characterSpan.LineCount, characterSpan.LastLineLength)
        | VisualSpan.Line range ->
            StoredVisualSpan.Line range.Count
        | VisualSpan.Block blockSpan -> 
            StoredVisualSpan.Block (blockSpan.Width, blockSpan.Height)

/// Contains information about an executed Command.  This instance *will* be stored
/// for long periods of time and used to repeat a Command instance across multiple
/// IVimBuffer instances so it simply cannot store any state specific to an 
/// ITextView instance.  It must be completely agnostic of such information 
[<RequireQualifiedAccess>]
type StoredCommand =

    /// The stored information about a NormalCommand
    | NormalCommand of NormalCommand * CommandData * CommandFlags

    /// The stored information about a VisualCommand
    | VisualCommand of VisualCommand * CommandData * StoredVisualSpan * CommandFlags

    /// The stored information about a InsertCommand
    | InsertCommand of InsertCommand * CommandFlags

    /// A Linked Command links together 2 other StoredCommand objects so they
    /// can be repeated together.
    | LinkedCommand of StoredCommand * StoredCommand

    with

    /// The CommandFlags associated with this StoredCommand
    member x.CommandFlags =
        match x with 
        | NormalCommand (_, _, flags) -> flags
        | VisualCommand (_, _, _, flags) -> flags
        | InsertCommand (_, flags) -> flags
        | LinkedCommand (_, rightCommand) -> rightCommand.CommandFlags

    /// Returns the last command.  For most StoredCommand values this is just an identity 
    /// function but for LinkedCommand values it returns the right most
    member x.LastCommand =
        match x with
        | NormalCommand _ -> x
        | VisualCommand _ -> x
        | InsertCommand _ -> x
        | LinkedCommand (_, right) -> right.LastCommand

    /// Create a StoredCommand instance from the given Command value
    static member OfCommand command (commandBinding : CommandBinding) = 
        match command with 
        | Command.NormalCommand (command, data) -> 
            StoredCommand.NormalCommand (command, data, commandBinding.CommandFlags)
        | Command.VisualCommand (command, data, visualSpan) ->
            let storedVisualSpan = StoredVisualSpan.OfVisualSpan visualSpan
            StoredCommand.VisualCommand (command, data, storedVisualSpan, commandBinding.CommandFlags)
        | Command.InsertCommand command ->
            StoredCommand.InsertCommand (command, commandBinding.CommandFlags)

/// Flags about specific motions
[<RequireQualifiedAccess>]
[<System.Flags>]
type MotionFlags =

    | None = 0x0

    /// This type of motion can be used to move the caret
    | CaretMovement = 0x1 

    /// The motion function wants to specially handle the esape function.  This is used 
    /// on Complex motions such as / and ? 
    | HandlesEscape = 0x2

    /// Text object selection motions.  These can be used for cursor movement inside of 
    /// Visual Mode but otherwise need to be used only after operators.  
    /// :help text-objects
    | TextObject = 0x4

    /// Text object with line to character.  Requires TextObject
    | TextObjectWithLineToCharacter = 0x8

    /// Text object with always character.  Requires TextObject
    | TextObjectWithAlwaysCharacter = 0x10

    /// Text objcet with always line.  Requires TextObject
    | TextObjectWithAlwaysLine = 0x12

/// Represents the types of MotionCommands which exist
[<RequireQualifiedAccess>]
type MotionBinding =

    /// Simple motion which comprises of a single KeyInput and a function which given 
    /// a start point and count will produce the motion.  None is returned in the 
    /// case the motion is not valid
    | Simple of KeyInputSet * MotionFlags * Motion

    /// Complex motion commands take more than one KeyInput to complete.  For example 
    /// the f,t,F and T commands all require at least one additional input.  The bool
    /// in the middle of the tuple indicates whether or not the motion can be 
    /// used as a cursor movement operation  
    | Complex of KeyInputSet * MotionFlags * BindDataStorage<Motion>

    with

    member x.KeyInputSet = 
        match x with
        | Simple (name, _, _) -> name
        | Complex (name, _, _) -> name

    member x.MotionFlags =
        match x with 
        | Simple (_, flags, _) -> flags
        | Complex (_, flags, _) -> flags

/// The information about the particular run of a Command
type CommandRunData = {

    /// The binding which the command was invoked from
    CommandBinding : CommandBinding

    /// The Command which was run
    Command : Command

    /// The result of the Command Run
    CommandResult : CommandResult

}

type CommandRunDataEventArgs(_commandRunData : CommandRunData) =
    inherit System.EventArgs()

    member x.CommandRunData = _commandRunData

/// Responsible for binding key input to a Motion and MotionArgument tuple.  Does
/// not actually run the motions
type IMotionCapture =

    /// Associated ITextView
    abstract TextView : ITextView
    
    /// Set of MotionBinding values supported
    abstract MotionBindings : seq<MotionBinding>

    /// Get the motion and count starting with the given KeyInput
    abstract GetMotionAndCount : KeyInput -> BindResult<Motion * int option>

    /// Get the motion with the provided KeyInput
    abstract GetMotion : KeyInput -> BindResult<Motion>

module CommandUtil2 = 

    let CountOrDefault opt = 
        match opt with 
        | Some(count) -> count
        | None -> 1

/// Responsible for managing a set of Commands and running them
type ICommandRunner =

    /// Set of Commands currently supported
    abstract Commands : CommandBinding seq

    /// In certain circumstances a specific type of key remapping needs to occur for input.  This 
    /// option will have the appropriate value in those circumstances.  For example while processing
    /// the {char} argument to f,F,t or T the Language mapping will be used
    abstract KeyRemapMode : KeyRemapMode option

    /// Is the command runner currently binding a command which needs to explicitly handly escape
    abstract IsHandlingEscape : bool

    /// True if waiting on more input
    abstract IsWaitingForMoreInput : bool

    /// Add a Command.  If there is already a Command with the same name an exception will
    /// be raised
    abstract Add : CommandBinding -> unit

    /// Remove a command with the specified name
    abstract Remove : KeyInputSet -> unit

    /// Process the given KeyInput.  If the command completed it will return a result.  A
    /// None value implies more input is needed to finish the operation
    abstract Run : KeyInput -> BindResult<CommandRunData>

    /// If currently waiting for more input on a Command, reset to the 
    /// initial state
    abstract ResetState : unit -> unit

    /// Raised when a command is successfully run
    [<CLIEvent>]
    abstract CommandRan : IDelegateEvent<System.EventHandler<CommandRunDataEventArgs>>

/// Information about a single key mapping
type KeyMapping = {

    // The LHS of the key mapping
    Left : KeyInputSet

    // The RHS of the key mapping
    Right : KeyInputSet 

    // Does the expansion partipciate in remapping
    AllowRemap : bool
}

/// Manages the key map for Vim.  Responsible for handling all key remappings
type IKeyMap =

    /// Get all mappings for the specified mode
    abstract GetKeyMappingsForMode : KeyRemapMode -> KeyMapping list

    /// Get the mapping for the provided KeyInput for the given mode.  If no mapping exists
    /// then a sequence of a single element containing the passed in key will be returned.  
    /// If a recursive mapping is detected it will not be persued and treated instead as 
    /// if the recursion did not exist
    abstract GetKeyMapping : KeyInputSet -> KeyRemapMode -> KeyMappingResult

    /// Map the given key sequence without allowing for remaping
    abstract MapWithNoRemap : lhs : string -> rhs : string -> KeyRemapMode -> bool

    /// Map the given key sequence allowing for a remap 
    abstract MapWithRemap : lhs : string -> rhs : string -> KeyRemapMode -> bool

    /// Unmap the specified key sequence for the specified mode
    abstract Unmap : lhs : string -> KeyRemapMode -> bool

    /// Unmap the specified key sequence for the specified mode by considering
    /// the passed in value to be an expansion
    abstract UnmapByMapping : righs : string -> KeyRemapMode -> bool

    /// Clear the Key mappings for the specified mode
    abstract Clear : KeyRemapMode -> unit

    /// Clear the Key mappings for all modes
    abstract ClearAll : unit -> unit

/// Jump list information associated with an IVimBuffer.  This is maintained as a forward
/// and backwards traversable list of points with which to navigate to
///
/// TODO:  Technically Vim's implementation of a jump list can span across different
/// buffers  This is limited to just a single ITextBuffer.  This is mostly due to Visual 
/// Studio's limitations in swapping out an ITextBuffer contents for a different file.  It
/// is possible but currently not a high priority here
type IJumpList = 

    /// Associated ITextView instance
    abstract TextView : ITextView

    /// Current value in the jump list.  Will be None if we are not currently traversing the
    /// jump list
    abstract Current : SnapshotPoint option

    /// Current index into the jump list.  Will be None if we are not currently traversing
    /// the jump list
    abstract CurrentIndex : int option

    /// True if we are currently traversing the list
    abstract IsTraversing : bool

    /// Get all of the jumps in the jump list.  Returns in order of most recent to oldest
    abstract Jumps : VirtualSnapshotPoint list

    /// The SnapshotPoint when the last jump occurred
    abstract LastJumpLocation : VirtualSnapshotPoint option

    /// Add a given SnapshotPoint to the jump list.  This will reset Current to point to 
    /// the begining of the jump list
    abstract Add : SnapshotPoint -> unit

    /// Clear out all of the stored jump information.  Removes all tracking information from
    /// the IJumpList
    abstract Clear : unit -> unit

    /// Move to the previous point in the jump list.  This will fail if we are not traversing
    /// the list or at the end 
    abstract MoveOlder : int -> bool

    /// Move to the next point in the jump list.  This will fail if we are not traversing
    /// the list or at the start
    abstract MoveNewer : int -> bool

    /// Set the last jump location to the given line and column
    abstract SetLastJumpLocation : line : int -> column : int -> unit

    /// Start a traversal of the list
    abstract StartTraversal : unit -> unit

type IIncrementalSearch = 

    /// True when a search is occurring
    abstract InSearch : bool

    /// When in the middle of a search this will return the SearchData for 
    /// the search
    abstract CurrentSearchData : SearchData option

    /// When in the middle of a search this will return the SearchResult for the 
    /// search
    abstract CurrentSearchResult : SearchResult option

    /// The ITextStructureNavigator used for finding 'word' values in the ITextBuffer
    abstract WordNavigator : ITextStructureNavigator

    /// Begin an incremental search in the ITextBuffer
    abstract Begin : Path -> BindData<SearchResult>

    [<CLIEvent>]
    abstract CurrentSearchUpdated : IDelegateEvent<System.EventHandler<SearchResultEventArgs>>

    [<CLIEvent>]
    abstract CurrentSearchCompleted : IDelegateEvent<System.EventHandler<SearchResultEventArgs>>

    [<CLIEvent>]
    abstract CurrentSearchCancelled : IDelegateEvent<System.EventHandler<SearchDataEventArgs>>

type RecordRegisterEventArgs(_register : Register, _isAppend : bool) =
    inherit System.EventArgs()
    
    member x.Register = _register

    member x.IsAppend = _isAppend

/// Used to record macros in a Vim 
type IMacroRecorder =

    /// The current recording 
    abstract CurrentRecording : KeyInput list option

    /// Is a macro currently recording
    abstract IsRecording : bool

    /// Start recording a macro into the specified Register.  Will fail if the recorder
    /// is already recording
    abstract StartRecording : Register -> isAppend : bool -> unit

    /// Stop recording a macro.  Will fail if it's not actually recording
    abstract StopRecording : unit -> unit

    /// Raised when a macro recording is started.  Passes the Register where the recording
    /// will take place.  The bool is whether the record is an append or not
    [<CLIEvent>]
    abstract RecordingStarted : IDelegateEvent<System.EventHandler<RecordRegisterEventArgs>>

    /// Raised when a macro recording is completed.
    [<CLIEvent>]
    abstract RecordingStopped : IDelegateEvent<System.EventHandler>

[<RequireQualifiedAccess>]
type ProcessResult = 

    /// The input was processed and provided the given ModeSwitch
    | Handled of ModeSwitch

    /// The input was processed but more input is needed in order to complete
    /// an operation
    | HandledNeedMoreInput

    /// The operation did not handle the input
    | NotHandled

    /// The input was processed and resulted in an error
    | Error

    /// Is this any type of mode switch
    member x.IsAnySwitch =
        match x with
        | Handled modeSwitch ->
            match modeSwitch with
            | ModeSwitch.NoSwitch -> false
            | ModeSwitch.SwitchMode _ -> true
            | ModeSwitch.SwitchModeWithArgument _ -> true
            | ModeSwitch.SwitchPreviousMode -> true
            | ModeSwitch.SwitchModeOneTimeCommand _ -> true
        | HandledNeedMoreInput ->
            false
        | NotHandled -> 
            false
        | Error -> 
            false

    /// Did this actually handle the KeyInput
    member x.IsAnyHandled = 
        match x with
        | Handled _ -> true
        | HandledNeedMoreInput -> true
        | Error -> true
        | NotHandled -> false

    /// Is this a successfully handled value?
    member x.IsHandledSuccess =
        match x with
        | Handled _ -> true
        | HandledNeedMoreInput -> true
        | Error -> false
        | NotHandled -> false

    static member OfModeKind kind = 
        let switch = ModeSwitch.SwitchMode kind
        Handled switch

    /// Create a ProcessResult from the given CommandResult value
    static member OfCommandResult commandResult = 
        match commandResult with
        | CommandResult.Completed modeSwitch -> Handled modeSwitch
        | CommandResult.Error -> Error

type StringEventArgs(_message : string) =
    inherit System.EventArgs()

    member x.Message = _message

type KeyInputEventArgs (_keyInput : KeyInput) = 
    inherit System.EventArgs()

    member x.KeyInput = _keyInput

type KeyInputSetEventArgs (_keyInputSet : KeyInputSet) = 
    inherit System.EventArgs()

    member x.KeyInputSet = _keyInputSet

type KeyInputProcessedEventArgs(_keyInput : KeyInput, _processResult : ProcessResult) =
    inherit System.EventArgs()

    member x.KeyInput = _keyInput

    member x.ProcessResult = _processResult

type SettingKind =
    | NumberKind
    | StringKind
    | ToggleKind

type SettingValue =
    | NumberValue of int
    | StringValue of string
    | ToggleValue of bool
    | CalculatedValue of (unit -> SettingValue)

    /// Get the AggregateValue of the SettingValue.  This will dig through any CalculatedValue
    /// instances and return the actual value
    member x.AggregateValue = 

        let rec digThrough value = 
            match value with 
            | CalculatedValue(func) -> digThrough (func())
            | _ -> value
        digThrough x

[<DebuggerDisplay("{Name}={Value}")>]
type Setting = {
    Name : string
    Abbreviation : string
    Kind : SettingKind
    DefaultValue : SettingValue
    Value : SettingValue
    IsGlobal : bool
} with 

    member x.AggregateValue = x.Value.AggregateValue

    /// Is the value calculated
    member x.IsValueCalculated =
        match x.Value with
        | CalculatedValue(_) -> true
        | _ -> false

    /// Is the setting value currently set to the default value
    member x.IsValueDefault = 
        match x.Value, x.DefaultValue with
        | CalculatedValue(_), CalculatedValue(_) -> true
        | NumberValue(left), NumberValue(right) -> left = right
        | StringValue(left), StringValue(right) -> left = right
        | ToggleValue(left), ToggleValue(right) -> left = right
        | _ -> false

module GlobalSettingNames = 

    let BackspaceName = "backspace"
    let CaretOpacityName = "vsvimcaret"
    let ClipboardName = "clipboard"
    let HighlightSearchName = "hlsearch"
    let HistoryName = "history"
    let IgnoreCaseName = "ignorecase"
    let IncrementalSearchName = "incsearch"
    let JoinSpacesName = "joinspaces"
    let KeyModelName = "keymodel"
    let MagicName = "magic"
    let MaxMapCount =  "vsvim_maxmapcount"
    let MaxMapDepth =  "maxmapdepth"
    let MouseModelName = "mousemodel"
    let ParagraphsName = "paragraphs"
    let ScrollOffsetName = "scrolloff"
    let SectionsName = "sections"
    let SelectionName = "selection"
    let SelectModeName = "selectmode"
    let ShellName = "shell"
    let ShellFlagName = "shellcmdflag"
    let ShiftWidthName = "shiftwidth"
    let SmartCaseName = "smartcase"
    let StartOfLineName = "startofline"
    let TildeOpName = "tildeop"
    let TimeoutExName = "ttimeout"
    let TimeoutName = "timeout"
    let TimeoutLengthName = "timeoutlen"
    let TimeoutLengthExName = "ttimeoutlen"
    let UseEditorIndentName = "vsvim_useeditorindent"
    let UseEditorSettingsName = "vsvim_useeditorsettings"
    let VisualBellName = "visualbell"
    let VirtualEditName = "virtualedit"
    let VimRcName = "vimrc"
    let VimRcPathsName = "vimrcpaths"
    let WrapScanName = "wrapscan"

module LocalSettingNames =

    let AutoIndentName = "autoindent"
    let ExpandTabName = "expandtab"
    let NumberName = "number"
    let NumberFormatsName = "nrformats"
    let TabStopName = "tabstop"
    let QuoteEscapeName = "quoteescape"

module WindowSettingNames =

    let CursorLineName = "cursorline"
    let ScrollName = "scroll"

/// Types of number formats supported by CTRL-A CTRL-A
[<RequireQualifiedAccess>]
type NumberFormat =
    | Alpha
    | Decimal
    | Hex
    | Octal

type SettingEventArgs(_setting : Setting) =
    inherit System.EventArgs()

    member x.Setting = _setting

/// The options which can be set in the 'clipboard' setting
type ClipboardOptions = 
    | None = 0
    | Unnamed = 0x1 
    | AutoSelect = 0x2
    | AutoSelectMl = 0x4

/// The options which can be set in the 'selectmode' setting
type SelectModeOptions =
    | None = 0
    | Mouse = 0x1
    | Keyboard = 0x2
    | Command = 0x4

/// The options which can be set in the 'keymodel' setting
type KeyModelOptions =
    | None = 0
    | StartSelection = 0x1
    | StopSelection = 0x2

/// Represent the setting supported by the Vim implementation.  This class **IS** mutable
/// and the values will change.  Setting names are case sensitive but the exposed property
/// names tend to have more familiar camel case names
type IVimSettings =

    /// Returns a sequence of all of the settings and values
    abstract AllSettings : Setting seq

    /// Try and set a setting to the passed in value.  This can fail if the value does not 
    /// have the correct type.  The provided name can be the full name or abbreviation
    abstract TrySetValue : settingName:string -> value:SettingValue -> bool

    /// Try and set a setting to the passed in value which originates in string form.  This 
    /// will fail if the setting is not found or the value cannot be converted to the appropriate
    /// value
    abstract TrySetValueFromString : settingName:string -> strValue:string -> bool

    /// Get the value for the named setting.  The name can be the full setting name or an 
    /// abbreviation
    abstract GetSetting : settingName:string -> Setting option

    /// Raised when a Setting changes
    [<CLIEvent>]
    abstract SettingChanged : IDelegateEvent<System.EventHandler<SettingEventArgs>>

and IVimGlobalSettings = 

    /// The multi-value option for determining backspace behavior.  Valid values include 
    /// indent, eol, start.  Usually accessed through the IsBackSpace helpers
    abstract Backspace : string with get, set

    /// Opacity of the caret.  This must be an integer between values 0 and 100 which
    /// will be converted into a double for the opacity of the caret
    abstract CaretOpacity : int with get, set

    /// The clipboard option.  Use the IsClipboard helpers for finding out if specific options 
    /// are set
    abstract Clipboard : string with get, set

    /// The parsed set of clipboard options
    abstract ClipboardOptions : ClipboardOptions with get, set

    /// Whether or not to highlight previous search patterns matching cases
    abstract HighlightSearch : bool with get,set

    /// The number of items to keep in the history lists
    abstract History : int with get, set

    /// Whether or not the magic option is set
    abstract Magic : bool with get,set

    /// Maximum number of maps which can occur for a key map.  This is not a standard vim or gVim
    /// setting.  It's a hueristic setting meant to prevent infinite recursion in the specific cases
    /// that maxmapdepth can't or won't catch (see :help maxmapdepth).  
    abstract MaxMapCount : int with get, set

    /// Maximum number of recursive depths which occur for a mapping
    abstract MaxMapDepth : int with get, set

    /// Whether or not we should be ignoring case in the ITextBuffer
    abstract IgnoreCase : bool with get, set

    /// Whether or not incremental searches should be highlighted and focused 
    /// in the ITextBuffer
    abstract IncrementalSearch : bool with get, set

    /// Is the 'indent' option inside of Backspace set
    abstract IsBackspaceIndent : bool with get

    /// Is the 'eol' option inside of Backspace set
    abstract IsBackspaceEol : bool with get

    /// Is the 'start' option inside of Backspace set
    abstract IsBackspaceStart : bool with get

    /// Is the 'onemore' option inside of VirtualEdit set
    abstract IsVirtualEditOneMore : bool with get

    /// Is the Selection setting set to a value which calls for inclusive 
    /// selection.  This does not directly track if Setting = "inclusive" 
    /// although that would cause this value to be true
    abstract IsSelectionInclusive : bool with get

    /// Is the Selection setting set to a value which permits the selection
    /// to extend past the line
    abstract IsSelectionPastLine : bool with get

    /// Whether or not to insert two spaces after certain constructs in a 
    /// join operation
    abstract JoinSpaces : bool with get, set

    /// The 'keymodel' setting
    abstract KeyModel : string with get, set

    /// The 'keymodel' in a type safe form
    abstract KeyModelOptions : KeyModelOptions with get, set

    /// The 'mousemodel' setting
    abstract MouseModel : string with get, set

    /// The nrooff macros that separate paragraphs
    abstract Paragraphs : string with get, set

    /// The nrooff macros that separate sections
    abstract Sections : string with get, set

    /// The name of the shell to use for shell commands
    abstract Shell : string with get, set

    /// The flag which is passed to the shell when executing shell commands
    abstract ShellFlag : string with get, set

    abstract ShiftWidth : int with get, set

    abstract StartOfLine : bool with get, set

    /// Controls the behavior of ~ in normal mode
    abstract TildeOp : bool with get,set

    /// Part of the control for key mapping and code timeout
    abstract Timeout : bool with get, set

    /// Part of the control for key mapping and code timeout
    abstract TimeoutEx : bool with get, set

    /// Timeout for a key mapping in milliseconds
    abstract TimeoutLength : int with get, set

    /// Timeout control for key mapping / code
    abstract TimeoutLengthEx : int with get, set

    /// Holds the scroll offset value which is the number of lines to keep visible
    /// above the cursor after a move operation
    abstract ScrollOffset : int with get, set

    /// Holds the Selection option
    abstract Selection : string with get, set

    /// Get the SelectionKind for the current settings
    abstract SelectionKind : SelectionKind

    /// Options for how select mode is entered
    abstract SelectMode : string with get, set 

    /// The options which are set via select mode
    abstract SelectModeOptions : SelectModeOptions with get, set

    /// Overrides the IgnoreCase setting in certain cases if the pattern contains
    /// any upper case letters
    abstract SmartCase : bool with get,set

    /// Let the editor control indentation of lines instead.  Overrides the AutoIndent
    /// setting
    abstract UseEditorIndent : bool with get, set

    /// Use the editor tab setting over the ExpandTab one
    abstract UseEditorSettings : bool with get, set

    /// Retrieves the location of the loaded VimRC file.  Will be the empty string if the load 
    /// did not succeed or has not been tried
    abstract VimRc : string with get, set

    /// Set of paths considered when looking for a .vimrc file.  Will be the empty string if the 
    /// load has not been attempted yet
    abstract VimRcPaths : string with get, set

    /// Holds the VirtualEdit string.  
    abstract VirtualEdit : string with get,set

    /// Whether or not to use a visual indicator of errors instead of a beep
    abstract VisualBell : bool with get,set

    /// Whether or not searches should wrap at the end of the file
    abstract WrapScan : bool with get, set

    /// The key binding which will cause all IVimBuffer instances to enter disabled mode
    abstract DisableAllCommand: KeyInput;

    inherit IVimSettings

/// Settings class which is local to a given IVimBuffer.  This will hide the work of merging
/// global settings with non-global ones
and IVimLocalSettings =

    abstract AutoIndent : bool with get, set

    /// Whether or not to expand tabs into spaces
    abstract ExpandTab : bool with get, set

    /// Return the handle to the global IVimSettings instance
    abstract GlobalSettings : IVimGlobalSettings

    /// Whether or not to put the numbers on the left column of the display
    abstract Number : bool with get, set

    /// Fromats that vim considers a number for CTRL-A and CTRL-X
    abstract NumberFormats : string with get, set

    /// How many spaces a tab counts for 
    abstract TabStop : int with get, set

    /// Which characters escape quotes for certain motion types
    abstract QuoteEscape : string with get, set

    /// Is the provided NumberFormat supported by the current options
    abstract IsNumberFormatSupported : NumberFormat -> bool

    inherit IVimSettings

/// Settings which are local to a given window.
and IVimWindowSettings = 

    /// Whether or not to highlight the line the cursor is on
    abstract CursorLine : bool with get, set

    /// Return the handle to the global IVimSettings instance
    abstract GlobalSettings : IVimGlobalSettings

    /// The scroll size 
    abstract Scroll : int with get, set

    inherit IVimSettings

/// Implements a list for storing history items.  This is used for the 5 types
/// of history lists in Vim (:help history).  
type HistoryList () = 

    let mutable _list : string list = List.empty
    let mutable _limit = Constants.DefaultHistoryLength

    /// Limit of the items stored in the list
    member x.Limit 
        with get () = 
            _limit
        and set value = 
            _limit <- value
            x.MaybeTruncateList()

    member x.Items = _list

    /// Adds an item to the top of the history list
    member x.Add value = 
        if not (StringUtil.isNullOrEmpty value) then
            let list =
                _list
                |> Seq.filter (fun x -> not (StringUtil.isEqual x value))
                |> Seq.truncate (_limit - 1)
                |> List.ofSeq
            _list <- value :: list

    /// Clear all of the items from the collection
    member x.Clear () = 
        _list <- List.empty

    member private x.MaybeTruncateList () = 
        if _list.Length > _limit then
            _list <-
                _list
                |> Seq.truncate _limit
                |> List.ofSeq

    interface System.Collections.IEnumerable with
        member x.GetEnumerator () = 
            let seq = _list :> string seq
            seq.GetEnumerator() :> System.Collections.IEnumerator

    interface System.Collections.Generic.IEnumerable<string> with
        member x.GetEnumerator () = 
            let seq = _list :> string seq
            seq.GetEnumerator()

/// Used for helping history editing 
type internal IHistoryClient<'TData, 'TResult> =

    /// History list used by this client
    abstract HistoryList : HistoryList

    /// What remapping mode if any should be used for key input
    abstract RemapMode : KeyRemapMode option

    /// Beep
    abstract Beep : unit -> unit

    /// Process the new command with the previous TData value
    abstract ProcessCommand : 'TData -> string -> 'TData

    /// Called when the command is completed.  The last valid TData and command
    /// string will be provided
    abstract Completed : 'TData -> string -> 'TResult

    /// Called when the command is cancelled.  The last valid TData value will
    /// be provided
    abstract Cancelled : 'TData -> unit

/// Represents shared state which is available to all IVimBuffer instances.
type IVimData = 

    /// The current directory Vim is positioned in
    abstract CurrentDirectory : string with get, set

    /// The history of the : command list
    abstract CommandHistory : HistoryList with get, set

    /// The ordered list of incremental search values
    abstract SearchHistory : HistoryList with get, set

    /// Motion function used with the last f, F, t or T motion.  The 
    /// first item in the tuple is the forward version and the second item
    /// is the backwards version
    abstract LastCharSearch : (CharSearchKind * Path * char) option with get, set

    /// The last command which was ran 
    abstract LastCommand : StoredCommand option with get, set

    /// The last shell command that was run
    abstract LastShellCommand : string option with get, set

    /// The last macro register which was run
    abstract LastMacroRun : char option with get, set

    /// Last pattern searched for in any buffer.
    abstract LastPatternData : PatternData with get, set

    /// Data for the last substitute command performed
    abstract LastSubstituteData : SubstituteData option with get, set

    /// The previous value of the current directory Vim is positioned in
    abstract PreviousCurrentDirectory : string

    /// Raise the highlight search one time disabled event
    abstract RaiseHighlightSearchOneTimeDisable : unit -> unit

    /// Raise the search occurred event
    abstract RaiseSearchRanEvent : unit -> unit

    /// Raised when a search is run on any IVimBuffer
    [<CLIEvent>]
    abstract SearchRan: IDelegateEvent<System.EventHandler>

    /// Raised when highlight search is disabled one time via the :noh command
    [<CLIEvent>]
    abstract HighlightSearchOneTimeDisabled : IDelegateEvent<System.EventHandler>

[<RequireQualifiedAccess>]
type QuickFix =
    | Next
    | Previous

type IVimHost =

    abstract Beep : unit -> unit

    /// Called at the start of a bulk operation such as a macro replay or a repeat of
    /// a last command
    abstract BeginBulkOperation : unit -> unit

    /// Close the provided view
    abstract Close : ITextView -> unit

    /// Create a hidden ITextView instance.  This is primarily used to load the contents
    /// of the vimrc
    abstract CreateHiddenTextView : unit -> ITextView

    /// Called at the end of a bulk operation such as a macro replay or a repeat of
    /// a last command
    abstract EndBulkOperation : unit -> unit

    /// Ensure that the given point is visible
    abstract EnsureVisible : textView : ITextView -> point : SnapshotPoint -> unit

    /// Format the provided lines
    abstract FormatLines : textView : ITextView -> range : SnapshotLineRange -> unit

    /// Get the ITextView which currently has keyboard focus
    abstract GetFocusedTextView : unit -> ITextView option

    /// Go to the definition of the value under the cursor
    abstract GoToDefinition : unit -> bool

    /// Go to the local declaration of the value under the cursor
    abstract GoToLocalDeclaration : textView : ITextView -> identifier : string -> bool

    /// Go to the local declaration of the value under the cursor
    abstract GoToGlobalDeclaration : tetxView : ITextView -> identifier : string -> bool

    /// Go to the "count" next tab window in the specified direction.  This will wrap 
    /// around
    abstract GoToNextTab : Path -> count : int -> unit

    /// Go the nth tab.  The first tab can be accessed with both 0 and 1
    abstract GoToTab : index : int -> unit

    /// Go to the specified entry in the quick fix list
    abstract GoToQuickFix : quickFix : QuickFix -> count : int -> hasBang : bool -> unit

    /// Get the name of the given ITextBuffer
    abstract GetName : textBuffer : ITextBuffer -> string

    /// Is the ITextBuffer in a dirty state?
    abstract IsDirty : textBuffer : ITextBuffer -> bool

    /// Is the ITextBuffer readonly
    abstract IsReadOnly : textBuffer : ITextBuffer -> bool

    /// Is the ITextView visible to the user
    abstract IsVisible : textView : ITextView -> bool

    /// Loads the new file into the existing window
    abstract LoadFileIntoExistingWindow : filePath : string -> textView : ITextView -> HostResult

    /// Loads the new file into a new existing window
    abstract LoadFileIntoNewWindow : filePath : string -> HostResult

    /// Run the host specific make operation
    abstract Make : jumpToFirstError : bool -> arguments : string -> HostResult

    /// Move to the view above the current one
    abstract MoveViewUp : ITextView -> unit

    /// Move to the view below the current one
    abstract MoveViewDown : ITextView -> unit

    /// Move to the view to the right of the current one
    abstract MoveViewRight : ITextView -> unit

    /// Move to the view to the right of the current one
    abstract MoveViewLeft : ITextView -> unit

    abstract NavigateTo : point : VirtualSnapshotPoint -> bool

    /// Quit the application
    abstract Quit : unit -> unit

    /// Reload the contents of the ITextBuffer discarding any changes
    abstract Reload : ITextBuffer -> bool

    /// Run the specified command with the given arguments and return the textual
    /// output
    abstract RunCommand : file : string -> arguments : string -> vimHost : IVimData -> string

    /// Run the Visual studio command
    abstract RunVisualStudioCommand : commandName : string -> argument : string -> unit

    /// Save the provided ITextBuffer instance
    abstract Save : ITextBuffer -> bool 

    /// Save the current document as a new file with the specified name
    abstract SaveTextAs : text:string -> filePath:string -> bool 

    /// Display the open file dialog 
    abstract ShowOpenFileDialog : unit -> unit

    /// Allow the host to custom process the insert command.  Hosts often have
    /// special non-vim semantics for certain types of edits (Enter for 
    /// example).  This override allows them to do this processing
    abstract TryCustomProcess : textView : ITextView -> command : InsertCommand -> bool

    /// Split the views horizontally
    abstract SplitViewHorizontally : ITextView -> HostResult

    /// Split the views horizontally
    abstract SplitViewVertically: ITextView -> HostResult

    /// Raised when the visibility of an ITextView changes
    [<CLIEvent>]
    abstract IsVisibleChanged : IDelegateEvent<System.EventHandler<TextViewEventArgs>>


/// Core parts of an IVimBuffer.  Used for components which make up an IVimBuffer but
/// need the same data provided by IVimBuffer.
type IVimBufferData =

    /// The current directory for this particular window
    abstract CurrentDirectory : string option with get, set

    /// This is the caret point at the start of the most recent visual mode session. It's
    /// the actual location of the caret vs. the anchor point.
    abstract VisualCaretStartPoint : ITrackingPoint option with get, set

    /// The IJumpList associated with the IVimBuffer
    abstract JumpList : IJumpList

    /// The ITextView associated with the IVimBuffer
    abstract TextView : ITextView

    /// The ITextBuffer associated with the IVimBuffer
    abstract TextBuffer : ITextBuffer

    /// The IStatusUtil associated with the IVimBuffer
    abstract StatusUtil : IStatusUtil

    /// The IUndoRedOperations associated with the IVimBuffer
    abstract UndoRedoOperations : IUndoRedoOperations

    /// The IVimTextBuffer associated with the IVimBuffer
    abstract VimTextBuffer : IVimTextBuffer

    /// The IVimWindowSettings associated with the ITextView 
    abstract WindowSettings : IVimWindowSettings

    /// The IWordUtil associated with the IVimBuffer
    abstract WordUtil : IWordUtil

    /// The IVimLocalSettings associated with the ITextBuffer
    abstract LocalSettings : IVimLocalSettings

    abstract Vim : IVim

/// Vim instance.  Global for a group of buffers
///
/// TODO: Change all of the MEF components to use a host controlled GetOrCreate 
/// method for IVimBuffer instances
and IVim =

    /// Buffer actively processing input.  This has no relation to the IVimBuffer
    /// which has focus 
    abstract ActiveBuffer : IVimBuffer option

    /// Whether or not the vimrc file should be autoloaded before the first IVimBuffer
    /// is created
    abstract AutoLoadVimRc : bool with get, set

    /// Get the set of tracked IVimBuffer instances
    abstract VimBuffers : IVimBuffer list

    /// Get the IVimBuffer which currently has KeyBoard focus
    abstract FocusedBuffer : IVimBuffer option

    /// Is the VimRc loaded
    abstract IsVimRcLoaded : bool

    /// In the middle of a bulk operation such as a macro replay or repeat last command
    abstract InBulkOperation : bool

    /// IKeyMap for this IVim instance
    abstract KeyMap : IKeyMap

    /// IMacroRecorder for the IVim instance
    abstract MacroRecorder : IMacroRecorder

    /// IMarkMap for the IVim instance
    abstract MarkMap : IMarkMap

    /// IRegisterMap for the IVim instance
    abstract RegisterMap : IRegisterMap

    /// ISearchService for this IVim instance
    abstract SearchService : ISearchService

    abstract GlobalSettings : IVimGlobalSettings

    abstract VimData : IVimData 

    abstract VimHost : IVimHost

    /// The Local Setting's which were persisted from loading VimRc.  If the 
    /// VimRc isn't loaded yet or if no VimRc was loaded a IVimLocalSettings
    /// with all default values will be returned.  Will store a copy of whatever 
    /// is passed in order to prevent memory leaks from captured ITextView`s
    abstract VimRcLocalSettings : IVimLocalSettings with get, set

    /// Create an IVimBuffer for the given ITextView
    abstract CreateVimBuffer : ITextView -> IVimBuffer

    /// Create an IVimTextBuffer for the given ITextBuffer
    abstract CreateVimTextBuffer : ITextBuffer -> IVimTextBuffer

    /// Close all IVimBuffer instances in the system
    abstract CloseAllVimBuffers : unit -> unit

    /// Get the IVimBuffer associated with the given ITextView
    abstract GetVimBuffer : ITextView -> IVimBuffer option

    /// Get the IVimTextBuffer associated with the given ITextBuffer
    abstract GetVimTextBuffer : ITextBuffer -> IVimTextBuffer option

    /// Get or create an IVimBuffer for the given ITextView
    abstract GetOrCreateVimBuffer : ITextView -> IVimBuffer

    /// Get or create an IVimTextBuffer for the given ITextBuffer
    abstract GetOrCreateVimTextBuffer : ITextBuffer -> IVimTextBuffer

    /// Load the VimRc file.  If the file was previously loaded a new load will be 
    /// attempted.  Returns true if a VimRc was actually loaded.
    ///
    /// If the VimRC is already loaded then it will be reloaded
    abstract LoadVimRc : unit -> bool

    /// Remove the IVimBuffer associated with the given view.  This will not actually close
    /// the IVimBuffer but instead just removes it's association with the given view
    abstract RemoveVimBuffer : ITextView -> bool

and SwitchModeKindEventArgs
    (
        _modeKind : ModeKind,
        _modeArgument : ModeArgument
    ) =
    inherit System.EventArgs()

    member x.ModeKind = _modeKind

    member x.ModeArgument = _modeArgument

and SwitchModeEventArgs 
    (
        _previousMode : IMode option,
        _currentMode : IMode
    ) = 

    inherit System.EventArgs()

    /// Current IMode 
    member x.CurrentMode = _currentMode

    /// Previous IMode.  Expressed as an Option because the first mode switch
    /// has no previous one
    member x.PreviousMode = _previousMode

and IMarkMap =

    /// The set of active global marks
    abstract GlobalMarks : (Letter * VirtualSnapshotPoint) seq

    /// Get the mark for the given char for the IVimTextBuffer
    abstract GetMark : mark : Mark -> vimBufferData : IVimBufferData -> VirtualSnapshotPoint option

    /// Get the current value of the specified global mark
    abstract GetGlobalMark : letter : Letter -> VirtualSnapshotPoint option

    /// Set the global mark to the given line and column in the provided IVimTextBuffer
    abstract SetGlobalMark : letter: Letter -> vimtextBuffer : IVimTextBuffer -> line : int -> column : int -> unit

    /// Set the mark for the given char for the IVimTextBuffer
    abstract SetMark : mark : Mark -> vimBufferData : IVimBufferData -> line : int -> column : int -> bool

    /// Delete all of the global marks 
    abstract ClearGlobalMarks : unit -> unit

/// This is the interface which represents the parts of a vim buffer which are shared amongst all
/// of it's views
and IVimTextBuffer = 

    /// The associated ITextBuffer instance
    abstract TextBuffer : ITextBuffer

    /// The associated IVimGlobalSettings instance
    abstract GlobalSettings : IVimGlobalSettings

    /// The last VisualSpan selection for the IVimTextBuffer.  This is a combination of a VisualSpan
    /// and the SnapshotPoint within the span where the caret should be positioned
    abstract LastVisualSelection : VisualSelection option with get, set

    /// The point the caret occupied when Insert mode was exitted 
    abstract LastInsertExitPoint : SnapshotPoint option with get, set

    /// The set of active local marks in the ITextBuffer
    abstract LocalMarks : (LocalMark * VirtualSnapshotPoint) seq

    /// The associated IVimLocalSettings instance
    abstract LocalSettings : IVimLocalSettings

    /// ModeKind of the current mode of the IVimTextBuffer.  It may seem odd at first to put ModeKind
    /// at this level but it is indeed shared amongst all views.  This can be demonstrated by opening
    /// the same file in multiple tabs, switch to insert in one and then move to the other via the
    /// mouse and noting it is also in Insert mode.  Actual IMode values are ITextView specific though
    /// and only live at the ITextView level
    abstract ModeKind : ModeKind

    /// Name of the buffer.  Used for items like Marks
    abstract Name : string

    /// The associated IVim instance
    abstract Vim : IVim

    /// The ITextStructureNavigator for word values in the ITextBuffer
    abstract WordNavigator : ITextStructureNavigator

    /// Get the local mark value 
    abstract GetLocalMark : localMark: LocalMark -> VirtualSnapshotPoint option

    /// Set the local mark value to the specified line and column.  Returns false if the given 
    /// mark cannot be set
    abstract SetLocalMark : localMark : LocalMark -> line : int -> column : int -> bool

    /// Switch the current mode to the provided value
    abstract SwitchMode : ModeKind -> ModeArgument -> unit

    /// Raised when the mode is switched.  Returns the old and new mode 
    [<CLIEvent>]
    abstract SwitchedMode : IDelegateEvent<System.EventHandler<SwitchModeKindEventArgs>>

/// Main interface for the Vim editor engine so to speak. 
and IVimBuffer =

    /// Sequence of available Modes
    abstract AllModes : seq<IMode>

    /// Buffered KeyInput list.  When a key remapping has multiple source elements the input 
    /// is buffered until it is completed or the ambiguity is removed.  
    abstract BufferedKeyInputs : KeyInput list

    /// The current directory for this particular window
    abstract CurrentDirectory : string option with get, set

    /// Global settings for the buffer
    abstract GlobalSettings : IVimGlobalSettings

    /// IIncrementalSearch instance associated with this IVimBuffer
    abstract IncrementalSearch : IIncrementalSearch

    /// Whether or not the IVimBuffer is currently processing a KeyInput value
    abstract IsProcessingInput : bool

    /// Is this IVimBuffer instance closed
    abstract IsClosed : bool

    /// Jump list
    abstract JumpList : IJumpList

    /// Local settings for the buffer
    abstract LocalSettings : IVimLocalSettings

    /// Associated IMarkMap
    abstract MarkMap : IMarkMap

    /// Current mode of the buffer
    abstract Mode : IMode

    /// ModeKind of the current IMode in the buffer
    abstract ModeKind : ModeKind

    /// Name of the buffer.  Used for items like Marks
    abstract Name : string

    /// If we are in the middle of processing a "one time command" (<c-o>) then this will
    /// hold the ModeKind which will be swiched back to after it's completed
    abstract InOneTimeCommand : ModeKind option

    /// Register map for IVim.  Global to all IVimBuffer instances but provided here
    /// for convenience
    abstract RegisterMap : IRegisterMap

    /// Underlying ITextBuffer Vim is operating under
    abstract TextBuffer : ITextBuffer

    /// Current ITextSnapshot of the ITextBuffer
    abstract TextSnapshot : ITextSnapshot

    /// View of the file
    abstract TextView : ITextView

    /// The IMotionUtil associated with this IVimBuffer instance
    abstract MotionUtil : IMotionUtil

    /// The IUndoRedoOperations associated with this IVimBuffer instance
    abstract UndoRedoOperations : IUndoRedoOperations

    /// Owning IVim instance
    abstract Vim : IVim

    /// Associated IVimTextBuffer
    abstract VimTextBuffer : IVimTextBuffer

    /// VimBufferData for the given IVimBuffer
    abstract VimBufferData : IVimBufferData

    /// The ITextStructureNavigator for word values in the buffer
    abstract WordNavigator : ITextStructureNavigator

    /// Associated IVimWindowSettings
    abstract WindowSettings : IVimWindowSettings

    /// Associated IVimData instance
    abstract VimData : IVimData

    /// INormalMode instance for normal mode
    abstract NormalMode : INormalMode

    /// ICommandMode instance for command mode
    abstract CommandMode : ICommandMode 

    /// IDisabledMode instance for disabled mode
    abstract DisabledMode : IDisabledMode

    /// IVisualMode for visual line mode
    abstract VisualLineMode : IVisualMode

    /// IVisualMode for visual block mode
    abstract VisualBlockMode : IVisualMode

    /// IVisualMode for visual character mode
    abstract VisualCharacterMode : IVisualMode

    /// IInsertMode instance for insert mode
    abstract InsertMode : IInsertMode

    /// IInsertMode instance for replace mode
    abstract ReplaceMode : IInsertMode

    /// ISelectMode instance for select mode
    abstract SelectMode: ISelectMode

    /// ISubstituteConfirmDoe instance for substitute confirm mode
    abstract SubstituteConfirmMode : ISubstituteConfirmMode

    /// IMode instance for external edits
    abstract ExternalEditMode : IMode

    /// Get the register of the given name
    abstract GetRegister : RegisterName -> Register

    /// Get the specified Mode
    abstract GetMode : ModeKind -> IMode

    /// Get the KeyInput value produced by this KeyInput in the current state of the
    /// IVimBuffer.  This will consider any buffered KeyInput values.
    abstract GetKeyInputMapping : KeyInput -> KeyMappingResult

    /// Process the KeyInput and return whether or not the input was completely handled
    abstract Process : KeyInput -> ProcessResult

    /// Process all of the buffered KeyInput values.
    abstract ProcessBufferedKeyInputs : unit -> unit

    /// Can the passed in KeyInput be processed by the current state of IVimBuffer.  The
    /// provided KeyInput will participate in remapping based on the current mode
    abstract CanProcess: KeyInput -> bool

    /// Can the passed in KeyInput be processed as a Vim command by the current state of
    /// the IVimBuffer.  The provided KeyInput will participate in remapping based on the
    /// current mode
    ///
    /// This is very similar to CanProcess except it will return false for any KeyInput
    /// which would be processed as a direct insert.  In other words commands like 'a',
    /// 'b' when handled by insert / replace mode
    abstract CanProcessAsCommand : KeyInput -> bool

    /// Switch the current mode to the provided value
    abstract SwitchMode : ModeKind -> ModeArgument -> IMode

    /// Switch the buffer back to the previous mode which is returned
    abstract SwitchPreviousMode : unit -> IMode

    /// Add a processed KeyInput value.  This is a way for a host which is intercepting 
    /// KeyInput and custom processing it to still participate in items like Macro 
    /// recording.  The provided value will not go through any remapping
    abstract SimulateProcessed : KeyInput -> unit

    /// Called when the view is closed and the IVimBuffer should uninstall itself
    /// and it's modes
    abstract Close : unit -> unit
    
    /// Raised when the mode is switched.  Returns the old and new mode 
    [<CLIEvent>]
    abstract SwitchedMode : IDelegateEvent<System.EventHandler<SwitchModeEventArgs>>

    /// Raised when a key is processed.  This is raised when the KeyInput is actually
    /// processed by Vim not when it is received.  
    ///
    /// Typically these occur back to back.  One example of where it does not though is 
    /// the case of a key remapping where the source mapping contains more than one key.  
    /// In this case the input is buffered until the second key is read and then the 
    /// inputs are processed
    [<CLIEvent>]
    abstract KeyInputProcessed : IDelegateEvent<System.EventHandler<KeyInputProcessedEventArgs>>

    /// Raised when a KeyInput is received by the buffer
    [<CLIEvent>]
    abstract KeyInputStart : IDelegateEvent<System.EventHandler<KeyInputEventArgs>>

    /// Raised when a key is received but not immediately processed.  Occurs when a
    /// key remapping has more than one source key strokes
    [<CLIEvent>]
    abstract KeyInputBuffered : IDelegateEvent<System.EventHandler<KeyInputSetEventArgs>>

    /// Raised when a KeyInput is completed processing within the IVimBuffer.  This happens 
    /// if the KeyInput is buffered or processed
    [<CLIEvent>]
    abstract KeyInputEnd : IDelegateEvent<System.EventHandler<KeyInputEventArgs>>

    /// Raised when a warning is encountered
    [<CLIEvent>]
    abstract WarningMessage : IDelegateEvent<System.EventHandler<StringEventArgs>>

    /// Raised when an error is encountered
    [<CLIEvent>]
    abstract ErrorMessage : IDelegateEvent<System.EventHandler<StringEventArgs>>

    /// Raised when a status message is encountered
    [<CLIEvent>]
    abstract StatusMessage : IDelegateEvent<System.EventHandler<StringEventArgs>>

    /// Raised when the IVimBuffer is being closed
    [<CLIEvent>]
    abstract Closed : IDelegateEvent<System.EventHandler>

    inherit IPropertyOwner

/// Interface for a given Mode of Vim.  For example normal, insert, etc ...
and IMode =

    /// Associated IVimTextBuffer
    abstract VimTextBuffer : IVimTextBuffer 

    /// What type of Mode is this
    abstract ModeKind : ModeKind

    /// Sequence of commands handled by the Mode.  
    abstract CommandNames : seq<KeyInputSet>

    /// Can the mode process this particular KeyIput at the current time
    abstract CanProcess : KeyInput -> bool

    /// Process the given KeyInput
    abstract Process : KeyInput -> ProcessResult

    /// Called when the mode is entered
    abstract OnEnter : ModeArgument -> unit

    /// Called when the mode is left
    abstract OnLeave : unit -> unit

    /// Called when the owning IVimBuffer is closed so that the mode can free up 
    /// any resources including event handlers
    abstract OnClose : unit -> unit

and INormalMode =

    /// Buffered input for the current command
    abstract Command : string 

    /// The ICommandRunner implementation associated with NormalMode
    abstract CommandRunner : ICommandRunner 

    /// Mode keys need to be remapped with currently
    abstract KeyRemapMode : KeyRemapMode option

    /// Is normal mode in the middle of a character replace operation
    abstract IsInReplace : bool

    inherit IMode

/// This is the interface implemented by Insert and Replace mode
and IInsertMode =

    /// The active IWordCompletionSession if one is active
    abstract ActiveWordCompletionSession : IWordCompletionSession option

    /// Is insert mode currently in a paste operation
    abstract IsInPaste : bool

    /// Is this KeyInput value considered to be a direct insert command in the current
    /// state of the IVimBuffer.  This does not apply to commands which edit the buffer
    /// like 'CTRL-D' but instead commands like 'a', 'b' which directly edit the 
    /// ITextBuffer
    abstract IsDirectInsert : KeyInput -> bool

    /// Raised when a command is successfully run
    [<CLIEvent>]
    abstract CommandRan : IDelegateEvent<System.EventHandler<CommandRunDataEventArgs>>

    inherit IMode

and ICommandMode = 

    /// buffered input for the current command
    abstract Command : string

    /// Run the specified command
    abstract RunCommand : string -> RunResult

    inherit IMode

and IVisualMode = 

    /// The ICommandRunner implementation associated with NormalMode
    abstract CommandRunner : ICommandRunner 

    /// Mode keys need to be remapped with currently
    abstract KeyRemapMode : KeyRemapMode option

    /// The current Visual Selection
    abstract VisualSelection : VisualSelection

    /// Asks Visual Mode to reset what it perceives to be the original selection.  Instead it 
    /// views the current selection as the original selection for entering the mode
    abstract SyncSelection : unit -> unit

    inherit IMode 
    
and IDisabledMode =
    
    /// Help message to display 
    abstract HelpMessage : string 

    inherit IMode

and ISelectMode = 

    /// Sync the selection with the current state
    abstract SyncSelection : unit -> unit

    inherit IMode

and ISubstituteConfirmMode =

    /// The SnapshotSpan of the current matching piece of text
    abstract CurrentMatch : SnapshotSpan option

    /// The string which will replace the current match
    abstract CurrentSubstitute : string option

    /// Raised when the current match changes
    [<CLIEvent>]
    abstract CurrentMatchChanged : IEvent<SnapshotSpan option> 

    inherit IMode 

[<Extension>]
module VimExtensions = 
    
    /// Is this ModeKind any type of Insert: Insert or Replace
    [<Extension>]
    let IsAnyInsert modeKind = 
        modeKind = ModeKind.Insert ||
        modeKind = ModeKind.Replace

module internal VimHostExtensions =
    type IVimHost with 
        member x.SaveAs (textView:ITextView) filePath = 
            x.SaveTextAs (textView.TextSnapshot.GetText()) filePath