# The compose deployer (deployer.ts): recreates compose services with new images. Built from this repo.
FROM oven/bun:1.4.3-slim
WORKDIR /app
COPY deployer.ts .
EXPOSE 8080
CMD ["bun", "deployer.ts"]
