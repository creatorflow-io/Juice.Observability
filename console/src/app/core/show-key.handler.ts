import { MissingTranslationHandler, MissingTranslationHandlerParams } from '@ngx-translate/core';

/** Without remote translations, a translation key is the English text itself. */
export class ShowKeyHandler implements MissingTranslationHandler {
  handle(params: MissingTranslationHandlerParams) {
    return params.key;
  }
}
