/*
 * content.js
 * ---------------------------------------------------------------------------
 * 这是本项目唯一的内容数据入口。
 * 日常修改 Post 正文、评论、时间、互动数、虚构用户资料或配图路径时，
 * 只修改本文件，不要改动 index.html / style.css / app.js。
 * ---------------------------------------------------------------------------
 */

var lunaaa = {
  displayName: "Cosimo Lunus",
  handle: "cosimolunus",
  avatarSrc: "assets/lunaaa.jpg",
  tone: "slate"
};

var lunaaaRelevant = Object.assign({}, lunaaa, {
  bio: "soft heart, loud laugh."
});

window.POST_DATA = {
  currentUser: {
    displayName: "User",
    handle: "user",
    tone: "slate"
  },

  meta: {
    defaultTitle: "Post / X",
    accountTitle: "Profile / X",
    searchTitle: "Search / X"
  },

  post: {
    id: "signal-47",
    author: lunaaa,
    text: "I’m so obsessed with Alex!! I just realized I was getting a cold and was googling what meds to get, and then she called me out of nowhere 😭\n\nShe brought my favorite food AND the medicine I needed. We’re literally mind readers. I love her soooo much!! <3<3",
    timestamp: "5:30 PM · Oct 22, 2026",
    datetime: "2026-10-22T17:30:00+08:00",
    views: 30300,
    replies: 5,
    reposts: 579,
    likes: 4600,
    bookmarks: 563,
    media: null
  },

  comments: [
    {
      id: "comment-emma-rae",
      author: {
        displayName: "emma rae",
        handle: "emmarae_xo",
        avatarSrc: "assets/comment-1.jpg",
        tone: "moss"
      },
      time: "32m",
      text: "Aww omg you’re so lucky 😭 I wish I had a friend who’d pop out of nowhere to check on me when I’m feeling sick too"
    },
    {
      id: "comment-sofia-lane",
      author: {
        displayName: "Sofia Lane",
        handle: "sofialane",
        avatarSrc: "assets/comment-2.jpg",
        tone: "clay"
      },
      time: "21m",
      text: "She’s such a keeper! You hit the jackpot with this friend！"
    },
    {
      id: "comment-chloe-b",
      author: {
        displayName: "chloe b.",
        handle: "chloebennett",
        avatarSrc: "assets/comment-3-giraffe.jpg",
        tone: "ink"
      },
      time: "17m",
      text: "If that was me, I’d straight up marry her😍"
    },
    {
      id: "comment-jamie-park",
      author: {
        displayName: "Jamie Park",
        handle: "jamiepark",
        avatarSrc: "assets/comment-4.jpg",
        tone: "slate"
      },
      time: "8m",
      text: "Isn’t that kinda creepy?"
    },
    {
      id: "comment-nina-alvarez",
      author: {
        displayName: "Nina Alvarez",
        handle: "ninaalvrz",
        avatarSrc: "assets/comment-5.jpg",
        tone: "clay"
      },
      time: "3m",
      text: "Last time I was googling what to eat, and she randomly hit me up to grab food! So happy to be her friend 🫶"
    }
  ],

  relevantPeople: [
    lunaaaRelevant
  ],

  accountNotFound: {
    label: "Profile",
    title: "This account doesn’t exist",
    message: "Try searching for another account, or check that the handle is correct.",
    searchLabel: "Search",
    backLabel: "Back to Post",
    note: "Local simulation · this route never contacts X."
  },

  search: {
    label: "Search",
    placeholder: "Search people or this post",
    hint: "Local search only. No request is sent to X.",
    emptyTitle: "Search this simulation",
    emptyText: "Try a name, a handle, or a phrase from the post.",
    noResultsTitle: "No results for",
    noResultsText: "Try a different name or a shorter phrase.",
    peopleHeading: "People",
    postHeading: "Post",
    resultHint: "Results are generated from content.js only."
  },

  unavailable: {
    notifications: {
      title: "Notifications",
      message: "Notifications are not part of this single-post demo. No account data is loaded and no alerts are fetched."
    },
    follow: {
      title: "Follow",
      message: "The Following timeline is not part of this demo. Only the Post view is included."
    },
    chat: {
      title: "Chat",
      message: "Messages are not included in this static simulation."
    },
    calls: {
      title: "Calls",
      message: "Calls are not included in this static simulation."
    },
    grok: {
      title: "Grok",
      message: "Grok is not connected to this project. No AI service is used."
    },
    history: {
      title: "History",
      message: "Post history is not included in this single-post demo."
    },
    premium: {
      title: "Premium",
      message: "Premium is not part of this simulation. No subscription or payment flow exists."
    },
    more: {
      title: "More",
      message: "The extended More menu is not part of this first version."
    }
  }
};
