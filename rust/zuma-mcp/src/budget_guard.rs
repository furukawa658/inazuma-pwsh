use serde_json::{Value, json};
use std::sync::Mutex;

#[derive(Clone, Debug)]
struct BudgetState {
    budget_chars: u64,
    used_chars: u64,
    workspace_key: Option<String>,
}

pub struct OutputBudget {
    state: Mutex<Option<BudgetState>>,
}

impl OutputBudget {
    pub fn new() -> Self {
        Self {
            state: Mutex::new(None),
        }
    }

    pub fn set(&self, arguments: &Value) -> Value {
        let Some(budget_chars) = arguments.get("budgetChars").and_then(Value::as_u64) else {
            return json!({
                "status": "error",
                "message": "budgetChars must be a positive integer."
            });
        };
        if budget_chars == 0 {
            return json!({
                "status": "error",
                "message": "budgetChars must be greater than zero."
            });
        }
        if arguments
            .get("budgetUnit")
            .and_then(Value::as_str)
            .is_some_and(|unit| unit != "characters")
        {
            return json!({
                "status": "error",
                "message": "budgetUnit supports characters only."
            });
        }
        let workspace_key = arguments
            .get("workspaceKey")
            .and_then(Value::as_str)
            .map(str::to_owned);
        *self.state.lock().unwrap() = Some(BudgetState {
            budget_chars,
            used_chars: 0,
            workspace_key: workspace_key.clone(),
        });
        json!({
            "status": "set",
            "budgetGuard": snapshot(budget_chars, 0, workspace_key.as_deref())
        })
    }

    pub fn clear(&self) -> Value {
        *self.state.lock().unwrap() = None;
        json!({ "status": "cleared", "budgetGuard": Value::Null })
    }

    pub fn decorate(&self, result: &mut Value, count_response: bool) {
        self.decorate_with_policy(result, count_response, false);
    }

    pub fn decorate_essential(&self, result: &mut Value) {
        self.decorate_with_policy(result, false, true);
    }

    fn decorate_with_policy(&self, result: &mut Value, count_response: bool, preserve_content: bool) {
        let response_chars = if count_response {
            result_chars(result) as u64
        } else {
            0
        };
        let budget = {
            let mut state = self.state.lock().unwrap();
            let Some(state) = state.as_mut() else {
                return;
            };
            state.used_chars = state.used_chars.saturating_add(response_chars);
            snapshot(
                state.budget_chars,
                state.used_chars,
                state.workspace_key.as_deref(),
            )
        };
        attach_budget(result, &budget, preserve_content);
    }
}

fn result_chars(result: &Value) -> usize {
    let Some(content) = result.get("content").and_then(Value::as_array) else {
        return 0;
    };
    content
        .iter()
        .filter_map(|item| item.get("text").and_then(Value::as_str))
        .map(|text| text.chars().count())
        .sum()
}

fn attach_budget(result: &mut Value, budget: &Value, preserve_content: bool) {
    let Some(content) = result
        .get_mut("content")
        .and_then(Value::as_array_mut)
    else {
        return;
    };
    for item in content {
        let Some(text) = item.get("text").and_then(Value::as_str) else {
            continue;
        };
        if let Ok(mut payload) = serde_json::from_str::<Value>(text)
            && let Some(object) = payload.as_object_mut()
        {
            if !preserve_content
                && budget.get("level").and_then(Value::as_str) == Some("exceeded")
            {
                let original_status = object
                    .get("status")
                    .and_then(Value::as_str)
                    .unwrap_or("unknown")
                    .to_string();
                payload = json!({
                    "status": "budget_exceeded",
                    "originalStatus": original_status,
                    "message": "Response details were omitted because the output budget was exhausted.",
                    "budgetGuard": budget.clone()
                });
            } else {
                object.insert("budgetGuard".to_string(), budget.clone());
            }
            item["text"] = Value::String(payload.to_string());
        }
    }
}

fn snapshot(budget_chars: u64, used_chars: u64, workspace_key: Option<&str>) -> Value {
    let remaining_chars = budget_chars.saturating_sub(used_chars);
    let percent_remaining = remaining_chars.saturating_mul(100) / budget_chars;
    let level = if used_chars >= budget_chars {
        "exceeded"
    } else if percent_remaining <= 10 {
        "critical"
    } else if percent_remaining <= 20 {
        "warning"
    } else {
        "normal"
    };
    let mut value = json!({
        "budgetUnit": "characters",
        "budgetChars": budget_chars,
        "usedChars": used_chars,
        "remainingChars": remaining_chars,
        "percentRemaining": percent_remaining,
        "level": level,
        "handoffRecommended": percent_remaining <= 10
    });
    if percent_remaining <= 20 {
        value["warning"] = Value::String(format!(
            "Budget warning: {percent_remaining}% remaining. Prepare handoff soon."
        ));
    }
    if let Some(workspace_key) = workspace_key {
        value["workspaceKey"] = Value::String(workspace_key.to_string());
    }
    value
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn warns_and_recommends_handoff_at_thresholds() {
        let warning = snapshot(100, 80, None);
        assert_eq!(warning["level"], "warning");
        assert_eq!(warning["percentRemaining"], 20);
        let critical = snapshot(100, 90, None);
        assert_eq!(critical["level"], "critical");
        assert_eq!(critical["handoffRecommended"], true);

        let budget = OutputBudget::new();
        budget.set(&json!({ "budgetChars": 100, "budgetUnit": "characters" }));
        let mut response = json!({
            "content": [{ "type": "text", "text": json!({ "status": "success", "value": "x" }).to_string() }]
        });
        budget.decorate(&mut response, true);
        let payload: Value =
            serde_json::from_str(response["content"][0]["text"].as_str().unwrap()).unwrap();
        assert!(payload["budgetGuard"]["usedChars"].as_u64().unwrap() > 0);
        budget.set(&json!({ "budgetChars": 1, "budgetUnit": "characters" }));
        let mut ordinary = json!({ "content": [{ "type": "text", "text": json!({ "status": "success", "details": "consume the budget" }).to_string() }] });
        budget.decorate(&mut ordinary, true);
        let ordinary_payload: Value =
            serde_json::from_str(ordinary["content"][0]["text"].as_str().unwrap()).unwrap();
        assert_eq!(ordinary_payload["status"], "budget_exceeded");

        let mut handoff = json!({ "content": [{ "type": "text", "text": json!({ "status": "success", "handoffText": "keep this handoff" }).to_string() }] });
        budget.decorate_essential(&mut handoff);
        let handoff_payload: Value =
            serde_json::from_str(handoff["content"][0]["text"].as_str().unwrap()).unwrap();
        assert_eq!(handoff_payload["handoffText"], "keep this handoff");
        assert_eq!(handoff_payload["budgetGuard"]["level"], "exceeded");

        assert_eq!(budget.clear()["status"], "cleared");
        let mut response = json!({ "content": [{ "type": "text", "text": json!({ "status": "success" }).to_string() }] });
        budget.decorate(&mut response, true);
        let payload: Value =
            serde_json::from_str(response["content"][0]["text"].as_str().unwrap()).unwrap();
        assert!(payload.get("budgetGuard").is_none());
    }

    #[test]
    fn rejects_invalid_budget_configuration() {
        let budget = OutputBudget::new();
        assert_eq!(budget.set(&json!({ "budgetChars": 0 }))["status"], "error");
        assert_eq!(
            budget.set(&json!({ "budgetChars": 10, "budgetUnit": "tokens" }))["status"],
            "error"
        );
    }
}
