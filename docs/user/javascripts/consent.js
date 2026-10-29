// Makes the cookie banner's Accept button mean "accept".
//
// The theme's banner saves the state of its checkboxes, and when it is opened
// again from the footer ("Change cookie settings") the boxes show the earlier
// choice. After a Reject they are unticked, so pressing Accept would save
// "rejected" again and appear to do nothing. This ticks them first. It only
// ever acts on the reader's own click on Accept; nothing runs before that.
document.addEventListener("click", function (event) {
  // The theme's Accept button has no type attribute; it defaults to "submit".
  var button = event.target.closest && event.target.closest(".md-consent button");
  if (!button || button.type !== "submit" || !button.form) return;
  button.form.querySelectorAll("input[type=checkbox][name]").forEach(function (box) {
    box.checked = true;
  });
}, true);
