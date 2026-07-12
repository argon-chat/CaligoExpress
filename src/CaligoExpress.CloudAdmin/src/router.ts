import { createRouter, createWebHistory } from "vue-router";
import { isAuthenticated } from "@/composables/useOAuth";

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: "/login", name: "login", component: () => import("@/pages/Login.vue"), meta: { public: true } },
    { path: "/callback", name: "callback", component: () => import("@/pages/Callback.vue"), meta: { public: true } },
    { path: "/", name: "dashboard", component: () => import("@/pages/Dashboard.vue") },
    { path: "/channels/:channel", name: "channel", component: () => import("@/pages/ChannelReleases.vue"), props: true },
    { path: "/settings", name: "settings", component: () => import("@/pages/Settings.vue") },
    { path: "/:pathMatch(.*)*", name: "notfound", component: () => import("@/pages/NotFound.vue"), meta: { public: true } },
  ],
});

router.beforeEach((to) => {
  if (to.meta.public) {
    if (to.name === "login" && isAuthenticated()) return { path: "/" };
    return true;
  }
  if (!isAuthenticated()) return { path: "/login" };
  return true;
});

export default router;
