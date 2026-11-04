"use client";
import { useEffect, useState } from "react";

export interface UserSettings {
  driverModel?: string;
  plannerModel?: string;
  maxCostUsd?: number;
  maxSteps?: number;
}

const STORAGE_KEY = "bughunter_user_settings";

export function loadUserSettings(): UserSettings {
  if (typeof window === "undefined") return {};
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? JSON.parse(raw) : {};
  } catch {
    return {};
  }
}

export function saveUserSettings(settings: UserSettings): void {
  if (typeof window === "undefined") return;
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
  } catch {
    // ignore
  }
}

export function useUserSettings(apiDefaults?: { driver?: string; planner?: string; maxCostUsd?: number }) {
  const [settings, setSettingsState] = useState<UserSettings>(() => loadUserSettings());

  useEffect(() => {
    const loaded = loadUserSettings();
    if (Object.keys(loaded).length > 0) {
      setSettingsState(loaded);
    } else if (apiDefaults) {
      setSettingsState({
        driverModel: apiDefaults.driver,
        plannerModel: apiDefaults.planner,
        maxCostUsd: apiDefaults.maxCostUsd ?? 1,
        maxSteps: 60,
      });
    }
  }, [apiDefaults]);

  const update = (newSettings: Partial<UserSettings>) => {
    const next = { ...settings, ...newSettings };
    setSettingsState(next);
    saveUserSettings(next);
  };

  return { settings, update };
}
