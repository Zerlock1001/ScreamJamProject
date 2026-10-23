/*
 * app.js
 * ---------------------------------------------------------------------------
 * 页面交互、hash 路由、模拟提示、图片放大与搜索。
 * 内容数据全部来自 content.js；本文件不保存任何 Post 文案。
 * ---------------------------------------------------------------------------
 */

(function () {
  "use strict";

  var SVG_NS = "http://www.w3.org/2000/svg";
  var data = window.POST_DATA;
  var root = document.getElementById("view-root");

  if (!data || !data.post || !root) {
    if (root) {
      root.textContent = "Unable to load content.js. Check that the file exists and is loaded before app.js.";
    }
    return;
  }

  var state = {
    route: { name: "post" },
    menuButton: null,
    lastFocus: null
  };

  var routeStack = [];
  var suppressNextHashPush = false;
  var toastTimer = null;

  var elements = {
    rightSearch: document.getElementById("search-input"),
    relevantList: document.getElementById("relevant-list"),
    lightbox: document.getElementById("lightbox"),
    lightboxImage: document.getElementById("lightbox-image"),
    lightboxCaption: document.getElementById("lightbox-caption"),
    lightboxClose: document.querySelector("[data-close-lightbox]"),
    modal: document.getElementById("feature-modal"),
    modalIcon: document.getElementById("feature-icon"),
    modalTitle: document.getElementById("feature-title"),
    modalBody: document.getElementById("feature-body"),
    modalNote: document.getElementById("feature-note"),
    toast: document.getElementById("toast")
  };

  // -------------------------------------------------------------------------
  // Small DOM helpers
  // -------------------------------------------------------------------------

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) {
      node.className = className;
    }
    if (text !== undefined) {
      node.textContent = text;
    }
    return node;
  }

  function createIcon(name, className) {
    var svg = document.createElementNS(SVG_NS, "svg");
    svg.setAttribute("class", className || "icon");
    svg.setAttribute("aria-hidden", "true");
    svg.setAttribute("focusable", "false");

    var use = document.createElementNS(SVG_NS, "use");
    use.setAttribute("href", "#icon-" + name);
    use.setAttributeNS("http://www.w3.org/1999/xlink", "xlink:href", "#icon-" + name);
    svg.appendChild(use);
    return svg;
  }

  function createAvatar(user, extraClass) {
    var tone = user && user.tone ? user.tone : "slate";
    var avatar = el("span", "avatar avatar--" + tone + (extraClass ? " " + extraClass : ""));
    avatar.setAttribute("aria-hidden", "true");

    if (user && user.avatarSrc) {
      var image = el("img", "avatar__image");
      image.src = user.avatarSrc;
      image.alt = "";
      image.decoding = "async";
      avatar.appendChild(image);
    } else {
      avatar.appendChild(createIcon("avatar"));
    }

    return avatar;
  }

  function profileHash(user) {
    return "#/user/" + encodeURIComponent(String(user.handle || ""));
  }

  function createUserLink(user, className, label) {
    var link = el("a", className, label);
    link.href = profileHash(user);
    return link;
  }

  function createAvatarLink(user) {
    var link = el("a", "avatar-link");
    link.href = profileHash(user);
    link.setAttribute("aria-label", "View " + user.displayName + "'s simulated profile");
    link.appendChild(createAvatar(user));
    return link;
  }

  function formatCount(value) {
    var number = Number(value);
    if (!Number.isFinite(number)) {
      return "0";
    }
    if (number < 1000) {
      return String(Math.round(number));
    }
    try {
      return new Intl.NumberFormat("en-US", {
        notation: "compact",
        compactDisplay: "short",
        maximumFractionDigits: 1
      }).format(number).replace(/\.0([KMB])$/, "$1");
    } catch (error) {
      return String(Math.round(number));
    }
  }

  function plural(count, singular, pluralWord) {
    return count === 1 ? singular : pluralWord || singular + "s";
  }

  // -------------------------------------------------------------------------
  // Routing
  // -------------------------------------------------------------------------

  function normalizeHash(hash) {
    if (!hash || hash === "#" || hash === "#/") {
      return "#/post";
    }
    return hash.indexOf("#/") === 0 ? hash : "#/" + hash.replace(/^#\/?/, "");
  }

  function currentHash() {
    return normalizeHash(window.location.hash);
  }

  function parseRoute(hash) {
    var clean = normalizeHash(hash).slice(1);
    var parts = clean.split("?");
    var segments = parts[0].split("/").filter(Boolean);
    var params = new URLSearchParams(parts[1] || "");

    if (segments[0] === "user") {
      var handle = segments[1] || "";
      try {
        handle = decodeURIComponent(handle);
      } catch (error) {
        handle = "";
      }
      return {
        name: "account",
        handle: handle.replace(/^@/, "")
      };
    }

    if (segments[0] === "search") {
      return {
        name: "search",
        query: params.get("q") || ""
      };
    }

    if (segments[0] === "unavailable") {
      return {
        name: "unavailable",
        id: segments[1] || "more"
      };
    }

    return { name: "post" };
  }

  function navigate(hash) {
    var target = normalizeHash(hash);
    if (currentHash() === target) {
      return;
    }
    window.location.hash = target;
  }

  function goBack() {
    if (routeStack.length > 1) {
      window.history.back();
      return;
    }

    if (state.route.name !== "post") {
      routeStack = ["#/post"];

      if (currentHash() === "#/post") {
        render();
        return;
      }

      try {
        window.history.replaceState(null, "", "#/post");
        render();
      } catch (error) {
        suppressNextHashPush = true;
        window.location.hash = "#/post";
      }
      return;
    }

    showToast("You're at the start of this demo.");
  }

  function onHashChange() {
    var hash = currentHash();

    if (suppressNextHashPush) {
      suppressNextHashPush = false;
    } else if (routeStack.length > 1 && hash === routeStack[routeStack.length - 2]) {
      routeStack.pop();
    } else if (routeStack[routeStack.length - 1] !== hash) {
      routeStack.push(hash);
    }

    render();
  }

  // -------------------------------------------------------------------------
  // Shared header / empty states
  // -------------------------------------------------------------------------

  function createHeader(title, showBack) {
    var header = el("header", "view-header");

    if (showBack !== false) {
      var back = el("button", "icon-button view-header__back");
      back.type = "button";
      back.setAttribute("data-action", "back");
      back.setAttribute("aria-label", "Back");
      back.appendChild(createIcon("back"));
      header.appendChild(back);
    }

    header.appendChild(el("h1", "view-header__title", title));
    return header;
  }

  function createBackToPostLink(label) {
    var link = el("a", "button button--secondary", label || "Back to Post");
    link.href = "#/post";
    return link;
  }

  function createEmptyState(options) {
    var section = el("section", "empty-page");
    var iconWrap = el("div", "empty-page__icon");
    iconWrap.appendChild(createIcon(options.icon || "info"));
    section.appendChild(iconWrap);

    if (options.title) {
      section.appendChild(el("h2", "empty-page__title", options.title));
    }
    if (options.text) {
      section.appendChild(el("p", "empty-page__text", options.text));
    }
    if (options.actions && options.actions.length) {
      var actions = el("div", "empty-page__actions");
      options.actions.forEach(function (action) {
        actions.appendChild(action);
      });
      section.appendChild(actions);
    }
    if (options.note) {
      section.appendChild(el("p", "empty-page__note", options.note));
    }

    return section;
  }

  // -------------------------------------------------------------------------
  // Post view
  // -------------------------------------------------------------------------

  function createPostMenu() {
    var menu = el("div", "post-menu");
    menu.id = "post-menu";
    menu.setAttribute("role", "menu");
    menu.setAttribute("aria-label", "Post actions");
    menu.hidden = true;

    var handle = data.post.author.handle;
    var items = [
      { action: "copy-link", icon: "link", label: "Copy link" },
      { separator: true },
      { action: "mute", icon: "mute", label: "Mute @" + handle },
      { action: "block", icon: "block", label: "Block @" + handle },
      { action: "report", icon: "flag", label: "Report post", danger: true }
    ];

    items.forEach(function (item) {
      if (item.separator) {
        menu.appendChild(el("div", "post-menu__sep"));
        return;
      }

      var button = el(
        "button",
        "post-menu__item" + (item.danger ? " post-menu__item--danger" : "")
      );
      button.type = "button";
      button.setAttribute("role", "menuitem");
      button.setAttribute("data-menu-action", item.action);
      button.appendChild(createIcon(item.icon));
      button.appendChild(el("span", "", item.label));
      menu.appendChild(button);
    });

    return menu;
  }

  function createPostAuthor() {
    var author = data.post.author;
    var row = el("div", "post__author");
    var identity = el("div", "post__identity");
    var nameLine = el("div", "post__name-line");

    nameLine.appendChild(createUserLink(author, "post__name", author.displayName));
    nameLine.appendChild(createUserLink(author, "post__handle", "@" + author.handle));
    identity.appendChild(nameLine);

    var more = el("button", "icon-button post__more");
    more.type = "button";
    more.setAttribute("data-action", "more");
    more.setAttribute("aria-label", "More post actions");
    more.setAttribute("aria-haspopup", "menu");
    more.setAttribute("aria-expanded", "false");
    more.setAttribute("aria-controls", "post-menu");
    more.appendChild(createIcon("more"));

    row.appendChild(createAvatarLink(author));
    row.appendChild(identity);
    row.appendChild(more);
    row.appendChild(createPostMenu());
    return row;
  }

  function createMediaButton() {
    var button = el("button", "post__media-button");
    button.type = "button";
    button.setAttribute("data-action", "open-media");
    button.setAttribute("aria-label", "Open image preview");
    button.setAttribute("aria-haspopup", "dialog");

    var image = el("img", "post__media");
    image.src = data.post.media.src;
    image.alt = data.post.media.alt;
    image.width = 1200;
    image.height = 1450;
    image.decoding = "async";

    button.appendChild(image);
    return button;
  }

  function createPostMeta() {
    var meta = el("div", "post__meta");
    var time = el("time");
    time.dateTime = data.post.datetime;
    time.textContent = data.post.timestamp;

    meta.appendChild(time);
    meta.appendChild(el("span", "post__meta-dot", "·"));
    meta.appendChild(el("span", "", formatCount(data.post.views) + " Views"));
    return meta;
  }

  function createActionButton(options) {
    var button = el("button", "action action--" + options.modifier);
    button.type = "button";
    button.setAttribute("data-action", options.action);
    if (options.ariaLabel) {
      button.setAttribute("aria-label", options.ariaLabel);
    }
    button.appendChild(createIcon(options.icon));
    if (options.count !== undefined) {
      button.appendChild(el("span", "action__count", formatCount(options.count)));
    }
    return button;
  }

  function createPostActions() {
    var actions = el("div", "post__actions");

    actions.appendChild(createActionButton({
      action: "focus-reply",
      modifier: "reply",
      icon: "reply",
      count: data.post.replies,
      ariaLabel: "Reply — focus the reply composer"
    }));

    actions.appendChild(createActionButton({
      action: "repost",
      modifier: "repost",
      icon: "repost",
      count: data.post.reposts,
      ariaLabel: "Repost, " + formatCount(data.post.reposts) + ", simulated"
    }));

    actions.appendChild(createActionButton({
      action: "like",
      modifier: "like",
      icon: "heart",
      count: data.post.likes,
      ariaLabel: "Like, " + formatCount(data.post.likes)
    }));

    actions.appendChild(createActionButton({
      action: "bookmark",
      modifier: "bookmark",
      icon: "bookmark",
      count: data.post.bookmarks,
      ariaLabel: "Bookmark, " + formatCount(data.post.bookmarks)
    }));

    actions.appendChild(createActionButton({
      action: "share",
      modifier: "share",
      icon: "share",
      ariaLabel: "Share"
    }));

    return actions;
  }

  function createReplyComposer() {
    var form = el("form", "reply-composer");
    form.setAttribute("data-reply-form", "");
    form.setAttribute("aria-label", "Reply composer");

    var currentUser = data.currentUser || {};
    form.appendChild(createAvatar({ tone: currentUser.tone || "slate" }));

    var body = el("div", "reply-composer__body");
    var textarea = el("textarea");
    textarea.name = "reply";
    textarea.rows = 1;
    textarea.maxLength = 280;
    textarea.placeholder = "Post your reply";
    textarea.setAttribute("aria-label", "Reply text");

    var actionRow = el("div", "reply-composer__actions");
    var reply = el("button", "reply-button", "Reply");
    reply.type = "submit";
    reply.disabled = true;
    actionRow.appendChild(reply);

    textarea.addEventListener("input", function () {
      reply.disabled = !textarea.value.trim();
      textarea.style.height = "auto";
      textarea.style.height = Math.min(textarea.scrollHeight, 220) + "px";
    });

    form.addEventListener("submit", function (event) {
      event.preventDefault();
      if (!textarea.value.trim()) {
        textarea.focus();
        return;
      }
      openFeatureModal({
        icon: "message",
        title: "Replies are read-only",
        body: "This static simulation cannot publish a reply. Your draft stays in the composer and nothing is sent anywhere.",
        note: "The replies below are fixed content from content.js."
      });
    });

    body.appendChild(textarea);
    body.appendChild(actionRow);
    form.appendChild(body);
    return form;
  }

  function createComments() {
    var section = el("section", "comments");
    section.setAttribute("aria-label", "Replies");

    var header = el("h2", "comments-header");
    header.appendChild(document.createTextNode("Replies"));
    header.appendChild(el("span", "comments-header__count", String(data.comments.length)));
    section.appendChild(header);

    data.comments.forEach(function (comment) {
      var article = el("article", "comment");
      var body = el("div", "comment__body");
      var head = el("div", "comment__head");

      head.appendChild(createUserLink(comment.author, "comment__name", comment.author.displayName));
      head.appendChild(createUserLink(comment.author, "comment__handle", "@" + comment.author.handle));
      head.appendChild(el("span", "comment__time", "· " + comment.time));

      body.appendChild(head);
      body.appendChild(el("p", "comment__text", comment.text));

      article.appendChild(createAvatarLink(comment.author));
      article.appendChild(body);
      section.appendChild(article);
    });

    return section;
  }

  function createPostView() {
    var fragment = document.createDocumentFragment();
    fragment.appendChild(createHeader("Post"));

    var article = el("article", "post");
    article.appendChild(createPostAuthor());
    article.appendChild(el("p", "post__text", data.post.text));
    if (data.post.media && data.post.media.src) {
      article.appendChild(createMediaButton());
    }
    article.appendChild(createPostMeta());
    article.appendChild(createPostActions());

    fragment.appendChild(article);
    fragment.appendChild(createReplyComposer());
    fragment.appendChild(createComments());
    return fragment;
  }

  // -------------------------------------------------------------------------
  // Account-not-found view
  // -------------------------------------------------------------------------

  function createAccountView(handle) {
    var fragment = document.createDocumentFragment();
    fragment.appendChild(createHeader(data.accountNotFound.label));

    var section = el("section", "account-page");
    section.appendChild(createAvatar({ tone: "slate" }, "avatar--lg"));
    section.appendChild(el("h2", "account-page__title", data.accountNotFound.title));
    section.appendChild(el("p", "account-page__text", data.accountNotFound.message));
    section.appendChild(el("span", "account-page__handle", "@" + (handle || "unknown")));

    var actions = el("div", "account-page__actions");
    var search = el("a", "button button--primary", data.accountNotFound.searchLabel);
    search.href = "#/search?q=" + encodeURIComponent(handle || "");
    actions.appendChild(search);
    actions.appendChild(createBackToPostLink(data.accountNotFound.backLabel));
    section.appendChild(actions);

    section.appendChild(el("p", "account-page__note", data.accountNotFound.note));
    fragment.appendChild(section);
    return fragment;
  }

  // -------------------------------------------------------------------------
  // Unavailable view
  // -------------------------------------------------------------------------

  function createUnavailableView(id) {
    var entry = data.unavailable[id] || {
      title: "Not included",
      message: "This page is not part of the single-post demo."
    };

    var fragment = document.createDocumentFragment();
    fragment.appendChild(createHeader(entry.title));
    fragment.appendChild(createEmptyState({
      icon: "info",
      title: "Not included in this demo",
      text: entry.message,
      actions: [createBackToPostLink("Back to Post")],
      note: "This is a local static simulation. Nothing is fetched from X."
    }));
    return fragment;
  }

  // -------------------------------------------------------------------------
  // Search view
  // -------------------------------------------------------------------------

  function uniqueUsers() {
    var users = [data.post.author]
      .concat(data.comments.map(function (comment) { return comment.author; }))
      .concat(data.relevantPeople || []);
    var seen = new Set();

    return users.filter(function (user) {
      if (!user || !user.handle || seen.has(user.handle)) {
        return false;
      }
      seen.add(user.handle);
      return true;
    });
  }

  function matchesQuery(values, query) {
    var haystack = values.join(" ").toLowerCase();
    return haystack.indexOf(query) !== -1;
  }

  function createSearchForm(query) {
    var form = el("form", "search search--page search-page__form");
    form.setAttribute("data-search-form", "");
    form.setAttribute("role", "search");

    var input = el("input");
    input.id = "search-page-input";
    input.name = "q";
    input.type = "search";
    input.autocomplete = "off";
    input.placeholder = data.search.placeholder;
    input.value = query || "";
    input.setAttribute("aria-label", "Search people or this post");

    var label = el("label", "visually-hidden", "Search people or this post");
    label.htmlFor = "search-page-input";

    form.appendChild(createIcon("search", "icon search__icon"));
    form.appendChild(label);
    form.appendChild(input);
    return form;
  }

  function createUserResult(user) {
    var link = el("a", "user-result");
    link.href = profileHash(user);

    var body = el("div", "user-result__body");
    var nameLine = el("div", "user-result__name-line");
    nameLine.appendChild(el("span", "user-result__name", user.displayName));
    nameLine.appendChild(el("span", "user-result__handle", "@" + user.handle));
    body.appendChild(nameLine);

    if (user.bio) {
      body.appendChild(el("p", "user-result__bio", user.bio));
    }

    link.appendChild(createAvatar(user));
    link.appendChild(body);
    return link;
  }

  function createSearchPost() {
    var link = el("a", "search-post");
    link.href = "#/post";

    var head = el("div", "search-post__head");
    head.appendChild(createAvatar(data.post.author));

    var headText = el("div", "search-post__head-text");
    headText.appendChild(el("span", "search-post__name", data.post.author.displayName));
    headText.appendChild(el("span", "search-post__handle", "@" + data.post.author.handle));
    headText.appendChild(el("span", "search-post__time", "· " + data.post.timestamp));
    head.appendChild(headText);

    link.appendChild(head);
    link.appendChild(el("p", "search-post__text", data.post.text));
    link.appendChild(el(
      "p",
      "search-post__meta",
      formatCount(data.post.views) + " Views · " + data.post.replies + " " +
        plural(data.post.replies, "reply", "replies")
    ));

    return link;
  }

  function createSearchView(query) {
    var fragment = document.createDocumentFragment();
    var cleanQuery = String(query || "").trim();
    fragment.appendChild(createHeader(data.search.label));

    var page = el("section", "search-page");
    page.appendChild(createSearchForm(cleanQuery));
    page.appendChild(el("p", "search-page__hint", data.search.hint));

    if (!cleanQuery) {
      page.appendChild(createEmptyState({
        icon: "search",
        title: data.search.emptyTitle,
        text: data.search.emptyText,
        note: data.search.resultHint
      }));
      fragment.appendChild(page);
      return fragment;
    }

    var needle = cleanQuery.toLowerCase().replace(/^@/, "");
    var people = uniqueUsers().filter(function (user) {
      return matchesQuery([user.displayName, user.handle, user.bio || ""], needle);
    });
    var postMatches = matchesQuery([
      data.post.text,
      data.post.author.displayName,
      data.post.author.handle,
      data.comments.map(function (comment) { return comment.text; }).join(" ")
    ], needle);

    page.appendChild(el("p", "search-summary", "Results for “" + cleanQuery + "”"));

    if (!people.length && !postMatches) {
      page.appendChild(createEmptyState({
        icon: "search",
        title: data.search.noResultsTitle + " “" + cleanQuery + "”",
        text: data.search.noResultsText,
        note: data.search.resultHint
      }));
      fragment.appendChild(page);
      return fragment;
    }

    if (people.length) {
      page.appendChild(el("h2", "result-heading", data.search.peopleHeading));
      people.forEach(function (user) {
        page.appendChild(createUserResult(user));
      });
    }

    if (postMatches) {
      page.appendChild(el("h2", "result-heading", data.search.postHeading));
      page.appendChild(createSearchPost());
    }

    fragment.appendChild(page);
    return fragment;
  }

  // -------------------------------------------------------------------------
  // Relevant people
  // -------------------------------------------------------------------------

  function renderRelevantPeople() {
    if (!elements.relevantList) {
      return;
    }

    elements.relevantList.replaceChildren();

    (data.relevantPeople || []).forEach(function (person) {
      var row = el("div", "relevant-person");
      var body = el("div", "relevant-person__body");
      var nameLine = el("div", "relevant-person__name-line");

      nameLine.appendChild(createUserLink(person, "relevant-person__name", person.displayName));
      nameLine.appendChild(createUserLink(person, "relevant-person__handle", "@" + person.handle));
      body.appendChild(nameLine);

      if (person.bio) {
        body.appendChild(el("p", "relevant-person__bio", person.bio));
      }

      var follow = el("button", "follow-button", "Follow");
      follow.type = "button";
      follow.setAttribute("data-action", "follow");
      follow.setAttribute("data-handle", person.handle);
      follow.setAttribute("aria-label", "Follow @" + person.handle + " (simulated)");

      row.appendChild(createAvatarLink(person));
      row.appendChild(body);
      row.appendChild(follow);
      elements.relevantList.appendChild(row);
    });
  }

  // -------------------------------------------------------------------------
  // Dynamic profile chip and nav state
  // -------------------------------------------------------------------------

  function renderAccountChip() {
    var currentUser = data.currentUser || data.post.author;
    var chip = document.querySelector("[data-account-chip]");
    var profileLink = document.querySelector("[data-profile-link]");

    if (chip) {
      chip.setAttribute("data-route", profileHash(currentUser));
      chip.setAttribute("aria-label", "View the simulated profile for " + currentUser.displayName);
      var avatar = chip.querySelector(".avatar");
      if (avatar) {
        avatar.className = "avatar avatar--" + (currentUser.tone || "slate");
        avatar.replaceChildren(createIcon("avatar"));
      }
      var name = chip.querySelector(".account-chip__name");
      var handle = chip.querySelector(".account-chip__handle");
      if (name) {
        name.textContent = currentUser.displayName;
      }
      if (handle) {
        handle.textContent = "@" + currentUser.handle;
      }
    }

    if (profileLink) {
      profileLink.href = profileHash(currentUser);
    }
  }

  function updateNav(route) {
    var active = null;

    if (route.name === "post") {
      active = "home";
    } else if (route.name === "search") {
      active = "explore";
    } else if (route.name === "unavailable") {
      active = route.id;
    } else if (route.name === "account") {
      var currentUser = data.currentUser || data.post.author;
      if (route.handle.toLowerCase() === String(currentUser.handle).toLowerCase()) {
        active = "profile";
      }
    }

    document.querySelectorAll("[data-nav]").forEach(function (item) {
      var isActive = item.getAttribute("data-nav") === active;
      item.classList.toggle("is-active", isActive);
      if (isActive) {
        item.setAttribute("aria-current", "page");
      } else {
        item.removeAttribute("aria-current");
      }
    });
  }

  function updateTitle(route) {
    if (route.name === "account") {
      document.title = "@" + (route.handle || "unknown") + " / X";
      return;
    }

    if (route.name === "search") {
      document.title = route.query
        ? "Search: " + route.query + " / X"
        : data.meta.searchTitle;
      return;
    }

    if (route.name === "unavailable") {
      var entry = data.unavailable[route.id];
      document.title = (entry ? entry.title : "Not included") + " / X";
      return;
    }

    document.title = data.meta.defaultTitle;
  }

  // -------------------------------------------------------------------------
  // Render
  // -------------------------------------------------------------------------

  function render() {
    closeMenu();
    if (!elements.modal.hidden) {
      closeFeatureModal(false);
    }
    if (!elements.lightbox.hidden) {
      closeLightbox(false);
    }

    var route = parseRoute(currentHash());
    state.route = route;

    var view;
    if (route.name === "account") {
      view = createAccountView(route.handle);
    } else if (route.name === "search") {
      view = createSearchView(route.query);
    } else if (route.name === "unavailable") {
      view = createUnavailableView(route.id);
    } else {
      view = createPostView();
    }

    root.replaceChildren(view);
    updateNav(route);
    updateTitle(route);

    if (route.name === "search" && elements.rightSearch) {
      elements.rightSearch.value = route.query;
    }

    window.scrollTo(0, 0);
  }

  // -------------------------------------------------------------------------
  // Modal and lightbox
  // -------------------------------------------------------------------------

  function openFeatureModal(options) {
    closeMenu();
    if (elements.modal.hidden) {
      state.lastFocus = document.activeElement;
    }

    elements.modalIcon.replaceChildren(createIcon(options.icon || "info"));
    elements.modalTitle.textContent = options.title || "Simulated action";
    elements.modalBody.textContent = options.body || "";
    elements.modalNote.textContent = options.note || "";
    elements.modalNote.hidden = !options.note;
    elements.modal.hidden = false;
    document.body.classList.add("no-scroll");

    var close = elements.modal.querySelector("[data-close-modal]");
    if (close) {
      close.focus();
    }
  }

  function closeFeatureModal(restoreFocus) {
    if (elements.modal.hidden) {
      return;
    }

    elements.modal.hidden = true;
    document.body.classList.remove("no-scroll");

    var previous = state.lastFocus;
    state.lastFocus = null;
    if (restoreFocus !== false && previous && document.contains(previous)) {
      previous.focus();
    }
  }

  function openLightbox(trigger) {
    if (!data.post.media || !data.post.media.src) {
      return;
    }

    state.lastFocus = trigger || document.activeElement;
    elements.lightboxImage.src = data.post.media.src;
    elements.lightboxImage.alt = data.post.media.alt;
    elements.lightboxCaption.textContent =
      data.post.author.displayName + " (@" + data.post.author.handle + ") · click outside or press Esc to close";
    elements.lightbox.hidden = false;
    document.body.classList.add("no-scroll");
    if (elements.lightboxClose) {
      elements.lightboxClose.focus();
    }
  }

  function closeLightbox(restoreFocus) {
    if (elements.lightbox.hidden) {
      return;
    }

    elements.lightbox.hidden = true;
    document.body.classList.remove("no-scroll");

    var previous = state.lastFocus;
    state.lastFocus = null;
    if (restoreFocus !== false && previous && document.contains(previous)) {
      previous.focus();
    }
  }

  function getFocusable(container) {
    var selector = [
      "a[href]",
      "button:not([disabled])",
      "input:not([disabled])",
      "textarea:not([disabled])",
      "select:not([disabled])",
      "[tabindex]:not([tabindex='-1'])"
    ].join(",");

    return Array.prototype.filter.call(
      container.querySelectorAll(selector),
      function (node) {
        return node.offsetParent !== null || node === document.activeElement;
      }
    );
  }

  function trapFocus(container, event) {
    if (event.key !== "Tab") {
      return;
    }

    var focusable = getFocusable(container);
    if (!focusable.length) {
      event.preventDefault();
      return;
    }

    var first = focusable[0];
    var last = focusable[focusable.length - 1];

    if (!container.contains(document.activeElement)) {
      event.preventDefault();
      first.focus();
      return;
    }

    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  // -------------------------------------------------------------------------
  // Menu
  // -------------------------------------------------------------------------

  function closeMenu() {
    if (state.menuButton) {
      state.menuButton.setAttribute("aria-expanded", "false");
    }
    document.querySelectorAll(".post-menu").forEach(function (menu) {
      menu.hidden = true;
    });
    state.menuButton = null;
  }

  function toggleMenu(button) {
    var menu = button.parentElement.querySelector(".post-menu");
    if (!menu) {
      return;
    }

    var shouldOpen = menu.hidden;
    closeMenu();

    if (shouldOpen) {
      menu.hidden = false;
      button.setAttribute("aria-expanded", "true");
      state.menuButton = button;
    }
  }

  // -------------------------------------------------------------------------
  // Clipboard and simulated notices
  // -------------------------------------------------------------------------

  function showToast(message) {
    if (!elements.toast) {
      return;
    }

    elements.toast.textContent = message;
    elements.toast.classList.add("is-visible");
    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(function () {
      elements.toast.classList.remove("is-visible");
    }, 2800);
  }

  async function copyText(text) {
    try {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
        return true;
      }
    } catch (error) {
      // Fall through to the selection-based fallback.
    }

    var area = el("textarea");
    area.value = text;
    area.setAttribute("readonly", "");
    area.style.position = "fixed";
    area.style.top = "-1000px";
    area.style.left = "-1000px";
    area.style.opacity = "0";
    document.body.appendChild(area);
    area.select();
    area.setSelectionRange(0, area.value.length);

    var copied = false;
    try {
      copied = document.execCommand("copy");
    } catch (error) {
      copied = false;
    }
    area.remove();
    return copied;
  }

  async function copyCurrentLink() {
    var link = window.location.href;
    var copied = await copyText(link);

    if (copied) {
      showToast("Link copied");
      return;
    }

    openFeatureModal({
      icon: "link",
      title: "Copy this link",
      body: link,
      note: "Clipboard access is blocked by the browser. Select the address and copy it manually."
    });
  }

  function openSimulatedInteraction(kind) {
    var copy = {
      like: {
        icon: "heart",
        title: "Likes are simulated",
        body: "This static page does not connect to X. The like count stays at " +
          formatCount(data.post.likes) + "."
      },
      repost: {
        icon: "repost",
        title: "Reposts are simulated",
        body: "This static page does not connect to X. The repost count stays at " +
          formatCount(data.post.reposts) + "."
      },
      bookmark: {
        icon: "bookmark",
        title: "Bookmarks are simulated",
        body: "This static page does not connect to X. The bookmark count stays at " +
          formatCount(data.post.bookmarks) + "."
      }
    }[kind];

    if (!copy) {
      return;
    }

    openFeatureModal({
      icon: copy.icon,
      title: copy.title,
      body: copy.body,
      note: "No request is sent and no data changes."
    });
  }

  function openFollowNotice(handle) {
    var target = handle || data.post.author.handle;
    openFeatureModal({
      icon: "user-plus",
      title: "Follow is simulated",
      body: "This static demo cannot follow @" + target + ". No follow request is sent to X.",
      note: "The button is here to preserve the layout, not to claim a real action."
    });
  }

  function openComposeNotice() {
    openFeatureModal({
      icon: "x",
      title: "Composer is not included",
      body: "This first version contains a single Post view. No new post can be published.",
      note: "Nothing is sent anywhere."
    });
  }

  function handleMenuAction(action) {
    var handle = data.post.author.handle;

    if (action === "copy-link") {
      copyCurrentLink();
      return;
    }

    if (action === "mute") {
      openFeatureModal({
        icon: "mute",
        title: "Mute is simulated",
        body: "This static page cannot mute @" + handle + ". No request is sent to X.",
        note: "No user or account data changes."
      });
      return;
    }

    if (action === "block") {
      openFeatureModal({
        icon: "block",
        title: "Block is simulated",
        body: "This static page cannot block @" + handle + ". No request is sent to X.",
        note: "No user or account data changes."
      });
      return;
    }

    if (action === "report") {
      openFeatureModal({
        icon: "flag",
        title: "Report is simulated",
        body: "This post and its author are fictional. No report is sent to X.",
        note: "The report flow exists only as a local notice."
      });
    }
  }

  // -------------------------------------------------------------------------
  // Event handling
  // -------------------------------------------------------------------------

  function focusReplyComposer() {
    var form = document.querySelector("[data-reply-form]");
    if (!form) {
      return;
    }

    var textarea = form.querySelector("textarea");
    var reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    form.scrollIntoView({
      behavior: reduceMotion ? "auto" : "smooth",
      block: "center"
    });
    if (textarea) {
      textarea.focus({ preventScroll: true });
    }
  }

  function handleAction(trigger) {
    var action = trigger.getAttribute("data-action");

    // Like, Bookmark, Share, Repost, and Follow are intentionally silent:
    // no feedback, no state change, and no navigation.
    if (
      action === "like" ||
      action === "bookmark" ||
      action === "share" ||
      action === "repost" ||
      action === "follow"
    ) {
      return;
    }

    if (action !== "more") {
      closeMenu();
    }

    if (action === "back") {
      goBack();
    } else if (action === "more") {
      toggleMenu(trigger);
    } else if (action === "open-media") {
      openLightbox(trigger);
    } else if (action === "focus-reply") {
      focusReplyComposer();
    } else if (action === "compose") {
      openComposeNotice();
    } else if (action === "skip") {
      var main = document.getElementById("main-content");
      if (main) {
        main.focus();
      }
    }
  }

  document.addEventListener("click", function (event) {
    if (event.target.closest("[data-close-modal]")) {
      event.preventDefault();
      closeFeatureModal();
      return;
    }

    if (event.target.closest("[data-close-lightbox]")) {
      event.preventDefault();
      closeLightbox();
      return;
    }

    var menuAction = event.target.closest("[data-menu-action]");
    if (menuAction) {
      event.preventDefault();
      var action = menuAction.getAttribute("data-menu-action");
      closeMenu();
      handleMenuAction(action);
      return;
    }

    var routeTrigger = event.target.closest("[data-route]");
    if (routeTrigger) {
      event.preventDefault();
      navigate(routeTrigger.getAttribute("data-route"));
      return;
    }

    var actionTrigger = event.target.closest("[data-action]");
    if (actionTrigger) {
      event.preventDefault();
      handleAction(actionTrigger);
      return;
    }

    if (state.menuButton) {
      closeMenu();
    }
  });

  document.addEventListener("submit", function (event) {
    var form = event.target.closest("[data-search-form]");
    if (!form) {
      return;
    }

    event.preventDefault();
    var input = form.querySelector('input[type="search"]');
    var query = input ? input.value.trim() : "";

    if (!query) {
      showToast("Enter a search term.");
      if (input) {
        input.focus();
      }
      return;
    }

    navigate("#/search?q=" + encodeURIComponent(query));
  });

  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape") {
      if (!elements.lightbox.hidden) {
        closeLightbox();
        return;
      }
      if (!elements.modal.hidden) {
        closeFeatureModal();
        return;
      }
      if (state.menuButton) {
        closeMenu();
        return;
      }
    }

    if (!elements.modal.hidden) {
      trapFocus(elements.modal, event);
      return;
    }

    if (!elements.lightbox.hidden) {
      trapFocus(elements.lightbox, event);
    }
  });

  elements.modal.addEventListener("click", function (event) {
    if (event.target === elements.modal) {
      closeFeatureModal();
    }
  });

  elements.lightbox.addEventListener("click", function (event) {
    if (event.target === elements.lightbox) {
      closeLightbox();
    }
  });

  window.addEventListener("hashchange", onHashChange);

  // -------------------------------------------------------------------------
  // Boot
  // -------------------------------------------------------------------------

  if (!window.location.hash) {
    try {
      window.history.replaceState(null, "", "#/post");
    } catch (error) {
      // The app still works with an empty hash; normalizeHash falls back to #/post.
    }
  }

  routeStack = [currentHash()];
  renderRelevantPeople();
  renderAccountChip();
  render();
})();
