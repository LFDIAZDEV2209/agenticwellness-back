"/ecs/cooppadresd-auth" as $historical |
"/ecs/cooppadresd-auth-ordinary" as $ordinary |
($enabled // "true") as $mode |
([.containerDefinitions[] | select(.name == "auth")]) as $auth |
if ($mode != "true" and $mode != "false") then
  error("AUTH_ORDINARY_LOGS_ENABLED_DEV must be true or false")
elif .family != "agenticwellness-auth" or ($auth | length) != 1 then
  error("expected one auth container in agenticwellness-auth")
elif ($auth[0].logConfiguration | .logDriver != "awslogs" or
      .options."awslogs-region" != "us-east-2" or
      (.options."awslogs-group" | . != $historical and . != $ordinary)) then
  error("unexpected Auth logging destination; refusing to overwrite it")
else
  (.containerDefinitions[] | select(.name == "auth") | .logConfiguration.options."awslogs-group") =
    (if $mode == "true" then $ordinary else $historical end)
end
