import {startPopup} from "./popup/controller.js";

// The entry point of the popup and of the side panel, which is the same page (see build.mjs). Its parts live in
// popup/: the controller owns the session and the live connection, the feed view renders the packets, the
// composer sends, and the toast shows every piece of feedback.
startPopup();
