# Teardown

Every resource the labs create is tagged `project=homebuild` (and a `lab` tag). Before ending any billed lab,
list what is left in the home Region:

```bash
aws resourcegroupstaggingapi get-resources --region eu-west-2 \
  --tag-filters Key=project,Values=homebuild \
  --query 'ResourceTagMappingList[].[ResourceARN, Tags[?Key==`lab`].Value | [0]]' --output table
```

Then check CloudFormation for stacks that are still there or failed to delete:

```bash
aws cloudformation list-stacks --region eu-west-2 \
  --stack-status-filter CREATE_COMPLETE UPDATE_COMPLETE DELETE_FAILED ROLLBACK_COMPLETE \
  --query 'StackSummaries[].[StackName, StackStatus]' --output table
```

Destroy app stacks with `npx cdk destroy --all` from `infra/`. The CDK bootstrap stack (`CDKToolkit`) is
not tagged and stays on purpose: one per account and Region, shared by every lab.

Two things the tag query does not show (C9):

- **IAM roles** (`homebuild-*`): the Resource Groups Tagging API does not list them. `aws iam list-roles --query "Roles[?starts_with(RoleName, 'homebuild')].RoleName"`.
- **Schedules** in the `homebuild` group (C9 Lab 6): the group is tagged, schedules cannot be. Expiry schedules (`homebuild-expiry-*`) are created by the API, not CloudFormation; deleting the group with `cdk destroy HomeBuild-Scheduling` deletes every schedule in it. Check with `aws scheduler list-schedules --group-name homebuild` first, and `aws scheduler list-schedules --name-prefix homebuild` afterwards.
