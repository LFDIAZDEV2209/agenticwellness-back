#!/usr/bin/env bash
# One explicit maintenance task, in the API network; no HTTP listener or messaging credentials.
set -euo pipefail
: "${AWS_REGION:?}" "${ECS_CLUSTER:?}" "${MEDICATION_DEMO_TASK_DEFINITION:?}"
[[ "$ECS_CLUSTER" == cooppadresd-cluster ]] || { echo 'Unexpected cluster'; exit 1; }
[[ "$MEDICATION_DEMO_TASK_DEFINITION" == arn:aws:ecs:us-east-2:933629770820:task-definition/cooppadresd-api:* ]] || { echo 'Unexpected API task definition'; exit 1; }
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
aws ecs describe-task-definition --task-definition "$MEDICATION_DEMO_TASK_DEFINITION" --region "$AWS_REGION" --query taskDefinition > "$WORK/base.json"
jq '{family:"cooppadresd-medication-demo",executionRoleArn,networkMode,requiresCompatibilities:["FARGATE"],cpu,memory,runtimePlatform,
 containerDefinitions:[.containerDefinitions[0] | {name:"medication-demo",image,essential:true,
 entryPoint:["dotnet","/app/medication-demo/CoppAddresd.MedicationDemoSeed.dll"],command:["--production"],
 environment:[{name:"COPP_MEDICATION_DEMO_CONFIRM",value:"SYNTHETIC_ACCOUNT_ONLY"}],
 secrets:[.secrets[] | select(.name=="ConnectionStrings__DefaultConnection")],logConfiguration}]} | with_entries(select(.value != null))' "$WORK/base.json" > "$WORK/demo.json"
TD=$(aws ecs register-task-definition --cli-input-json "file://$WORK/demo.json" --region "$AWS_REGION" --query taskDefinition.taskDefinitionArn --output text)
NET=$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services cooppadresd-api --region "$AWS_REGION" --query 'services[0].networkConfiguration' --output json)
aws ecs run-task --cluster "$ECS_CLUSTER" --task-definition "$TD" --region "$AWS_REGION" --launch-type FARGATE --network-configuration "$NET" > "$WORK/run.json"
TASK=$(jq -r '.tasks[0].taskArn // empty' "$WORK/run.json")
[[ -n "$TASK" && $(jq '.failures|length' "$WORK/run.json") == 0 ]] || { echo 'Maintenance task did not start'; exit 1; }
echo "Synthetic-only maintenance task: $TASK"
aws ecs wait tasks-stopped --cluster "$ECS_CLUSTER" --tasks "$TASK" --region "$AWS_REGION"
CODE=$(aws ecs describe-tasks --cluster "$ECS_CLUSTER" --tasks "$TASK" --region "$AWS_REGION" --query 'tasks[0].containers[0].exitCode' --output text)
[[ "$CODE" == 0 ]] || { echo "Synthetic seed failed (exit $CODE); existing passwords are never reset."; exit 1; }
echo 'Synthetic medication demo ready; credentials are never printed.'
