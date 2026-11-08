ALTER TABLE `events` ADD COLUMN `image_prompt_json` text;
--> statement-breakpoint
ALTER TABLE `events` ADD COLUMN `image_blob_key` text;
--> statement-breakpoint
ALTER TABLE `events` ADD COLUMN `image_status` text DEFAULT 'none' NOT NULL;
