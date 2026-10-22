import {startPopup} from "./popup/controller.js";

// The popup's entry point. Its parts live in popup/: the controller owns the session and the live connection,
// the feed view renders the packets, the composer sends, and the toast shows every piece of feedback.
startPopup();
